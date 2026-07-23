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

        var logo = await logos.GetContentAsync(ct);
        if (logo is null) return Results.NotFound();

        // Revalidate every time. A logo is replaced rarely but visibly, and any
        // max-age window is a window in which the farm sees the old one after
        // an admin swapped it. The ETag makes revalidation a 304, so the cost
        // of "no-cache" is a round trip, not a megabyte.
        //
        // `private` because this is tenant data behind an Authorization header:
        // no shared cache may keep a copy.
        http.Response.Headers.CacheControl = "private, no-cache";

        // Results.Bytes handles If-None-Match / If-Modified-Since itself. Doing
        // that here instead would mean writing conditional-request semantics by
        // hand to save one query on a request the SPA makes once per load.
        return Results.Bytes(
            logo.Content,
            contentType: logo.ContentType,
            // No fileDownloadName: that would set Content-Disposition
            // attachment, and this is rendered in an <img>, not downloaded.
            fileDownloadName: null,
            lastModified: logo.UpdatedAt,
            entityTag: new EntityTagHeaderValue($"\"{logo.ContentHash}\""));
    }

    private static async Task<IResult> SetLogo(
        SetFarmLogoHandler handler, TenantContext tenant, HttpContext http, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        // A declared oversize is refused without reading a byte. Content-Length
        // is only a claim, which is why the read below is capped as well.
        if (http.Request.ContentLength > ImageSanitizer.MaxByteLength)
            return MapFailure(ImageSanitizer.TooLarge);

        // Kestrel's default ceiling is 30 MB. Lowering it for this endpoint
        // stops an oversized upload at the transport rather than streaming it
        // into the process first. Kestrel then raises BadHttpRequestException
        // with a 413, which the /error handler already maps.
        var sizeLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
            sizeLimit.MaxRequestBodySize = ImageSanitizer.MaxByteLength;

        // One byte past the cap, so "exactly at the limit" and "over it" are
        // distinguishable without trusting Content-Length.
        //
        // THE LOOP BOUND IS THE MEMORY GUARANTEE, not the check after it: the
        // condition and the slice both stop at `cap`, so a body with no
        // declared length — or a lying one — is read to 1 MB and one byte and
        // no further, whatever the client intended to send.
        var cap = ImageSanitizer.MaxByteLength + 1;
        var buffer = ArrayPool<byte>.Shared.Rent(cap);
        try
        {
            var total = 0;
            int read;
            while (total < cap
                && (read = await http.Request.Body.ReadAsync(buffer.AsMemory(total, cap - total), ct)) > 0)
                total += read;

            // Deliberately the same verdict ImageSanitizer would reach on its
            // own length check — kept as an early exit so an oversize body
            // doesn't pay for a handler call and a database round trip, not
            // because the sanitizer needs the help.
            if (total > ImageSanitizer.MaxByteLength) return MapFailure(ImageSanitizer.TooLarge);

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
