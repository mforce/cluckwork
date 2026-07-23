namespace Cluckwork.Api.Endpoints.Accounts;

using System.Buffers;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Accounts.RemoveFarmLogo;
using Cluckwork.Application.Features.Accounts.SetFarmLogo;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Media;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

// #123 — the farm logo: upload, serve, remove.
//
// The upload takes a RAW body rather than multipart/form-data. Multipart would
// contribute only a filename and a declared content type, both of which this
// endpoint ignores on purpose (the format is sniffed from the bytes), in
// exchange for a parser running ahead of our code, a spill to a temp file above
// 64 KB, and an antiforgery exemption to justify. A raw PUT means the body is
// read here, under a cap set here.
public static class FarmLogoEndpoints
{
    public static RouteGroupBuilder MapFarmLogoEndpoints(this RouteGroupBuilder group)
    {
        // Open to every authenticated role: the logo is farm branding in the
        // SPA chrome, which a read-only viewer sees like anyone else. The two
        // writes below are admin-gated.
        group.MapGet("/logo", GetLogo)
            .WithName("GetFarmLogo")
            .WithSummary("The farm logo image. 404 when none is set; the chrome falls back to app branding.");

        group.MapPut("/logo", SetLogo)
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .WithName("SetFarmLogo")
            .WithSummary(
                "Upload or replace the farm logo. Raw image body (PNG/JPEG/WebP), 1 MB max. " +
                "The stored image is a rewritten copy with metadata and trailing bytes removed.");

        group.MapDelete("/logo", RemoveLogo)
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .WithName("RemoveFarmLogo")
            .WithSummary("Clear the farm logo.");

        return group;
    }

    private static async Task<IResult> GetLogo(
        IFarmLogoRepository logos, TenantContext tenant, HttpContext http, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        // Metadata first: the projection leaves the bytes in the database.
        var metadata = await logos.GetMetadataAsync(ct);
        if (metadata is null) return Results.NotFound();

        var etag = new EntityTagHeaderValue($"\"{metadata.ContentHash}\"");

        // Revalidate every time. A logo is replaced rarely but visibly, and any
        // max-age window is a window in which the farm sees the old one after
        // an admin swapped it. The ETag makes revalidation a 304, so the cost
        // of "no-cache" is a round trip, not a megabyte.
        //
        // `private` because this is tenant data behind an Authorization header:
        // no shared cache may keep a copy.
        http.Response.Headers.CacheControl = "private, no-cache";

        // Answered BEFORE the content query. Results.Bytes would reach the same
        // 304 on its own, but only after a megabyte had come out of Postgres to
        // be thrown away — and with `no-cache` this is the request browsers
        // make most. The tech spec says the bytes are read only on a cache
        // miss; this is what makes that true (codex review of #168).
        if (MatchesIfNoneMatch(http.Request, etag))
        {
            http.Response.Headers.ETag = etag.ToString();
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var logo = await logos.GetContentAsync(ct);
        // Removed between the two reads.
        if (logo is null) return Results.NotFound();

        // Results.Bytes still owns the rest of the conditional-request
        // semantics — If-Modified-Since, and If-None-Match for anything the
        // check above deliberately does not model.
        return Results.Bytes(
            logo.Content,
            contentType: logo.ContentType,
            // No fileDownloadName: that would set Content-Disposition
            // attachment, and this is rendered in an <img>, not downloaded.
            fileDownloadName: null,
            lastModified: logo.UpdatedAt,
            entityTag: new EntityTagHeaderValue($"\"{logo.ContentHash}\""));
    }

    // Weak comparison, per RFC 9110 for If-None-Match: a 304 only has to mean
    // "the representation you hold is still good", not "byte-identical
    // encoding". `*` matches any existing representation.
    //
    // Returns false — deferring to Results.Bytes — whenever this cannot be the
    // whole answer. RFC 9110 evaluates If-Match BEFORE If-None-Match, and a
    // failed If-Match is a 412; short-circuiting to 304 here would answer the
    // lower-precedence condition and skip the higher one entirely (codex round
    // 2 of #168). Those requests cost a byte load, which is the right price for
    // not reimplementing precedence.
    private static bool MatchesIfNoneMatch(HttpRequest request, EntityTagHeaderValue etag)
    {
        var headers = request.GetTypedHeaders();
        if (request.Headers.ContainsKey(HeaderNames.IfMatch)
            || request.Headers.ContainsKey(HeaderNames.IfUnmodifiedSince))
            return false;

        var candidates = headers.IfNoneMatch;
        if (candidates is null || candidates.Count == 0) return false;

        foreach (var candidate in candidates)
        {
            // Compared on the tag itself rather than through Any.Equals, so
            // this does not depend on the parser handing back the singleton.
            if (candidate.Tag == "*") return true;
            if (candidate.Compare(etag, useStrongComparison: false)) return true;
        }

        return false;
    }

    private static async Task<IResult> SetLogo(
        SetFarmLogoHandler handler, TenantContext tenant, HttpContext http, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        // A declared oversize is refused without reading a byte. Content-Length
        // is only a claim, which is why the read below is capped as well.
        if (http.Request.ContentLength > ImageSanitizer.MaxByteLength)
            return MapFailure(ImageSanitizer.TooLarge);

        // Kestrel's default ceiling is 30 MB; lowering it here cuts an oversized
        // upload off at the transport instead of streaming it into the process.
        // Best-effort only — the feature is absent under TestServer and turns
        // read-only once the body has been touched — so it is a nicety, never
        // the guarantee. The read loop below is the guarantee.
        var sizeLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
            sizeLimit.MaxRequestBodySize = ImageSanitizer.MaxByteLength;

        // THE LOOP BOUND IS THE MEMORY GUARANTEE: the condition and the slice
        // both stop at the cap, so a body with no declared length — or a lying
        // one — is read that far and no further, whatever the client meant to
        // send.
        //
        // Rented at exactly the cap, not cap+1. ArrayPool's largest bucket is
        // 1 MB, so asking for one byte more misses the pool entirely and
        // allocates a fresh megabyte on the heap every upload (pi review of
        // #168). The extra byte only ever existed to tell "exactly at the cap"
        // from "over it", and the probe read below does that without it.
        var buffer = ArrayPool<byte>.Shared.Rent(ImageSanitizer.MaxByteLength);
        try
        {
            var total = 0;
            int read;
            while (total < ImageSanitizer.MaxByteLength
                && (read = await http.Request.Body.ReadAsync(
                    buffer.AsMemory(total, ImageSanitizer.MaxByteLength - total), ct)) > 0)
                total += read;

            // A body that exactly filled the buffer might have more behind it.
            // One byte settles it.
            if (total == ImageSanitizer.MaxByteLength)
            {
                var probe = new byte[1];
                if (await http.Request.Body.ReadAsync(probe, ct) > 0)
                    return MapFailure(ImageSanitizer.TooLarge);
            }

            var result = await handler.HandleAsync(buffer.AsMemory(0, total), tenant.AccountId, ct);
            return result.IsSuccess
                ? Results.Ok(ToResponse(result.Value))
                : MapFailure(result.Error);
        }
        finally
        {
            // Cleared on return: the pool is process-wide, so a later rent by
            // another tenant's request must not be able to read the tail of
            // this one's upload.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<IResult> RemoveLogo(
        RemoveFarmLogoHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var result = await handler.HandleAsync(ct);
        if (result.IsSuccess) return Results.NoContent();
        return result.Error == RemoveFarmLogoHandler.NotSet
            ? Results.NotFound()
            : MapFailure(result.Error);
    }

    // Each failure keeps its code as the problem title, matching the settings
    // endpoint, so the SPA switches on a stable string rather than on prose.
    private static IResult MapFailure(Error error)
    {
        var status = error.Code switch
        {
            // The body is over the size the endpoint accepts.
            "FarmLogo.TooLarge" => StatusCodes.Status413PayloadTooLarge,
            // Not one of the three formats — a text file, a PDF, an SVG.
            "FarmLogo.UnsupportedFormat" => StatusCodes.Status415UnsupportedMediaType,
            // Right format, but the content breaks a rule: no bytes, a broken
            // container, or dimensions past the cap.
            _ => StatusCodes.Status422UnprocessableEntity
        };

        return Results.Problem(error.Description, statusCode: status, title: error.Code);
    }

    private static FarmLogoResponse ToResponse(FarmLogoMetadata m) =>
        new(m.ContentType, m.ContentHash, m.Width, m.Height, m.ByteLength, m.UpdatedAt);
}

// Describes the STORED image, so the SPA can show "PNG, 512x512, 24 KB" after
// an upload and know it is reporting what the server kept rather than what the
// browser sent.
public sealed record FarmLogoResponse(
    string ContentType,
    string ContentHash,
    int Width,
    int Height,
    int ByteLength,
    DateTimeOffset UpdatedAt);
