namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;
using Cluckwork.Api.Endpoints.Accounts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

// #179 — the farm-banner upload cap, mirroring FarmLogoRequestBodyCap.cs
// exactly (see that file for the full reasoning: why a middleware ahead of
// IdempotencyMiddleware, why not WithMaxRequestBodyBytes).
public static class FarmBannerRequestBodyCap
{
    public static IApplicationBuilder UseFarmBannerRequestBodyCap(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<FarmBannerUploadCapMetadata>() is null)
            {
                await next();
                return;
            }

            var maxBytes = context.RequestServices
                .GetRequiredService<IOptionsSnapshot<FarmBannerOptions>>().Value.MaxUploadBytes;

            if (context.Request.ContentLength > maxBytes)
            {
                await FarmBannerEndpoints.WriteTooLargeAsync(context, maxBytes);
                return;
            }

            var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false })
                sizeLimit.MaxRequestBodySize = maxBytes;

            context.Request.Body = new ByteCappedRequestStream(context.Request.Body, maxBytes);

            try
            {
                await next();
            }
            catch (BadHttpRequestException ex)
                when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
            {
                await FarmBannerEndpoints.WriteTooLargeAsync(context, maxBytes);
            }
        });
}

// Marker attached to PUT /account/banner; read by the middleware above.
public sealed record FarmBannerUploadCapMetadata;
