namespace Cluckwork.Api.Endpoints.Accounts;

using System.Buffers;
using Cluckwork.Api.Configuration;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Accounts.RemoveFarmBanner;
using Cluckwork.Application.Features.Accounts.SetFarmBanner;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Media;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Cluckwork.Api.Hosting;

// #179 — the farm banner: upload, serve, remove. Mirrors FarmLogoEndpoints.cs
// exactly, including the raw-body-PUT and ETag/304 reasoning documented there;
// only the asset (banner vs logo, its own size cap, its own error codes) and
// the route (/account/banner) differ.
public static class FarmBannerEndpoints
{
    public static RouteGroupBuilder MapFarmBannerEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/banner", GetBanner)
            .WithName("GetFarmBanner")
            .WithSummary("The farm banner image, shown on the post-login splash. 404 when none is set.");

        group.MapPut("/banner", SetBanner)
            .WithMetadata(new ReadsRequestBodyAttribute())
            .WithMetadata(new FarmBannerUploadCapMetadata())
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .WithName("SetFarmBanner")
            .WithSummary(
                "Upload or replace the farm banner. Raw image body (PNG/JPEG/WebP), capped by the " +
                "configured limit (5 MB by default).");

        group.MapDelete("/banner", RemoveBanner)
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .WithName("RemoveFarmBanner")
            .WithSummary("Clear the farm banner.");

        return group;
    }

    private static async Task<IResult> GetBanner(
        IFarmLogoRepository logos, TenantContext tenant, HttpContext http, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var metadata = await logos.GetBannerMetadataAsync(ct);
        if (metadata is null) return Results.NotFound();

        var etag = new EntityTagHeaderValue($"\"{metadata.ContentHash}\"");

        http.Response.Headers.CacheControl = "private, no-cache";

        if (FarmLogoEndpoints.MatchesIfNoneMatch(http.Request, etag))
        {
            http.Response.Headers.ETag = etag.ToString();
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var banner = await logos.GetBannerContentAsync(ct);
        if (banner is null) return Results.NotFound();

        return Results.Bytes(
            banner.Content,
            contentType: banner.ContentType,
            fileDownloadName: null,
            lastModified: banner.UpdatedAt,
            entityTag: new EntityTagHeaderValue($"\"{banner.ContentHash}\""));
    }

    private static async Task<IResult> SetBanner(
        SetFarmBannerHandler handler, IOptionsSnapshot<FarmBannerOptions> bannerOptions,
        TenantContext tenant, HttpContext http, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var maxBytes = bannerOptions.Value.MaxUploadBytes;

        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var total = 0;
            int read;
            while (total < maxBytes
                && (read = await http.Request.Body.ReadAsync(
                    buffer.AsMemory(total, maxBytes - total), ct)) > 0)
                total += read;

            if (total == maxBytes)
            {
                var probe = new byte[1];
                if (await http.Request.Body.ReadAsync(probe, ct) > 0)
                    return MapFailure(ImageSanitizer.TooLarge(maxBytes, ImageSanitizer.ImageAssetKind.Banner));
            }

            var result = await handler.HandleAsync(buffer.AsMemory(0, total), tenant.AccountId, maxBytes, ct);
            return result.IsSuccess
                ? Results.Ok(ToResponse(result.Value))
                : MapFailure(result.Error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<IResult> RemoveBanner(
        RemoveFarmBannerHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var result = await handler.HandleAsync(ct);
        if (result.IsSuccess) return Results.NoContent();
        return result.Error == RemoveFarmBannerHandler.NotSet
            ? Results.NotFound()
            : MapFailure(result.Error);
    }

    // Same status mapping as FarmLogoEndpoints.MapFailure, keyed off the
    // FarmBanner.* codes ImageSanitizer's asset-aware overload now returns.
    private static IResult MapFailure(Error error)
    {
        var status = error.Code switch
        {
            "FarmBanner.TooLarge" => StatusCodes.Status413PayloadTooLarge,
            "FarmBanner.UnsupportedFormat" => StatusCodes.Status415UnsupportedMediaType,
            _ => StatusCodes.Status422UnprocessableEntity
        };

        return Results.Problem(error.Description, statusCode: status, title: error.Code);
    }

    internal static Task WriteTooLargeAsync(HttpContext context, int maxBytes) =>
        MapFailure(ImageSanitizer.TooLarge(maxBytes, ImageSanitizer.ImageAssetKind.Banner)).ExecuteAsync(context);

    private static FarmLogoResponse ToResponse(FarmLogoMetadata m) =>
        new(m.ContentType, m.ContentHash, m.Width, m.Height, m.ByteLength, m.UpdatedAt);
}
