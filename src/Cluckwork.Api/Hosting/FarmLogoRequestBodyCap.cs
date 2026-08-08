namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;
using Cluckwork.Api.Endpoints.Accounts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

// #442 — the farm-logo upload cap, moved from INSIDE the SetLogo handler to a
// middleware registered before IdempotencyMiddleware (mirroring
// RequestBodyLimit.UseCluckworkRequestBodyLimit, right next to which this is
// registered in Program.cs).
//
// Why this couldn't just be WithMaxRequestBodyBytes: that mechanism's cap is a
// compile-time long baked into route metadata at startup. FarmLogoOptions.
// MaxUploadBytes is read from IOptionsSnapshot — reloadable per request — so it
// has to be resolved from THIS request's DI container, not registered once at
// route-mapping time.
//
// Why this couldn't just stay inline in SetLogo: IdempotencyMiddleware (#307)
// calls request.EnableBuffering() and reads the WHOLE body to hash it, for
// every idempotency-gated write, before the endpoint ever runs. An inline cap
// set inside the handler arms IHttpMaxRequestBodySizeFeature only after that
// full read already happened — Kestrel's feature is IsReadOnly by then, so the
// inline `sizeLimit.MaxRequestBodySize = maxBytes` silently no-ops, and a
// client that ignores the declared limit forces the server to buffer up to
// Kestrel's own much larger default before the endpoint's own read loop
// finally notices and rejects it.
//
// The fix: arm the SAME transport cutoff, and wrap the body in the SAME
// ByteCappedRequestStream RequestBodyLimit.cs already uses, from a middleware
// that runs BEFORE IdempotencyMiddleware. A declared Content-Length over the
// cap is refused immediately, before idempotency touches the body at all. A
// chunked/lying body that exceeds the cap while idempotency hashes it — or, if
// idempotency is ever bypassed, while SetLogo's own read loop drains it — throws
// from inside ByteCappedRequestStream; the try/catch below is what turns that
// throw back into FarmLogoEndpoints' existing FarmLogo.TooLarge response
// instead of letting it fall through to /error's generic "Invalid request
// body" mapping, so the response contract (and KestrelRequestBodyLimitTests'
// assertion on it) is unchanged — only WHEN the cap bites moves earlier.
public static class FarmLogoRequestBodyCap
{
    public static IApplicationBuilder UseFarmLogoRequestBodyCap(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<FarmLogoUploadCapMetadata>() is null)
            {
                await next();
                return;
            }

            var maxBytes = context.RequestServices
                .GetRequiredService<IOptionsSnapshot<FarmLogoOptions>>().Value.MaxUploadBytes;

            // Declared oversize, refused before idempotency reads a byte.
            if (context.Request.ContentLength > maxBytes)
            {
                await FarmLogoEndpoints.WriteTooLargeAsync(context, maxBytes);
                return;
            }

            // Best-effort transport cutoff — see ByteCappedRequestStream's own
            // comment for why this alone is not the guarantee.
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
                await FarmLogoEndpoints.WriteTooLargeAsync(context, maxBytes);
            }
        });
}

// Marker attached to PUT /account/logo; read by the middleware above.
public sealed record FarmLogoUploadCapMetadata;
