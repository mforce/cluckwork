namespace Cluckwork.Api.Hosting;

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

// #141 — cache policy for the SPA served from wwwroot. Vite emits content-hashed
// files under /assets/ (e.g. index-4af3c1.js): the name changes whenever the
// bytes do, so they are safe to cache forever ('immutable'). index.html is the
// unhashed entry point that names the current bundle, so it must always
// revalidate ('no-cache') — otherwise a client keeps booting an old app after a
// deploy. Everything else at the root (favicon, manifest, …) also revalidates.
//
// The same policy is applied to plain static-file responses AND to the SPA
// fallback (an unknown route rewritten to index.html), so both go out with the
// correct header. A fronting CDN (Cloudflare) can then respect these values:
// edge-cache the immutable assets forever, revalidate index.html, and — via its
// own rules — bypass /api/* entirely (dynamic responses carry no cache header).
public static class StaticAssetCaching
{
    // One year (the max practical value), paired with 'immutable' so browsers
    // skip even the conditional revalidation for content-hashed assets.
    public const string ImmutableAsset = "public, max-age=31536000, immutable";

    // Cacheable-but-revalidate: ETag/Last-Modified conditional GETs are still
    // allowed, but freshness is checked every time. Deliberately NOT 'no-store'.
    public const string AlwaysRevalidate = "no-cache";

    // The hashed-asset directory Vite writes to (served at /assets/...).
    private const string HashedAssetPath = "/assets";

    // Wired into StaticFileOptions.OnPrepareResponse for both the static-file
    // middleware and the SPA fallback. For the fallback the request path is the
    // unknown SPA route (e.g. /dashboard), never under /assets, so it correctly
    // lands on no-cache — matching a direct GET /index.html.
    public static void ApplyCacheHeaders(StaticFileResponseContext ctx)
    {
        var immutable = ctx.Context.Request.Path.StartsWithSegments(
            HashedAssetPath, StringComparison.OrdinalIgnoreCase);
        ctx.Context.Response.Headers[HeaderNames.CacheControl] =
            immutable ? ImmutableAsset : AlwaysRevalidate;
    }
}
