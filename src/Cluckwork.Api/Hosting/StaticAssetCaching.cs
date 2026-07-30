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
// correct header. A fronting CDN can then respect these values:
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

    // OnPrepareResponse for the plain static-file middleware, which only fires
    // once a physical file has been matched. So a served path under /assets IS a
    // real hashed asset -> immutable; anything else at the root (index.html,
    // favicon, manifest, …) revalidates. Ordinal (not IgnoreCase) to mirror the
    // case-sensitive file lookup on Linux: if a file was served, its request
    // path already matches the on-disk casing under /assets.
    public static void ApplyCacheHeaders(StaticFileResponseContext ctx)
    {
        var immutable = ctx.Context.Request.Path.StartsWithSegments(
            HashedAssetPath, StringComparison.Ordinal);
        ctx.Context.Response.Headers[HeaderNames.CacheControl] =
            immutable ? ImmutableAsset : AlwaysRevalidate;
    }

    // OnPrepareResponse for the SPA fallback, which ALWAYS serves index.html no
    // matter the requested URL (a missing /assets/x, an unknown route, …), so it
    // unconditionally revalidates. Deliberately does NOT key off the request
    // path: ASP.NET rewrites it to /index.html before serving, but relying on
    // that internal detail is exactly what makes this look wrong at a glance —
    // an explicit always-no-cache callback is correct regardless.
    public static void AlwaysRevalidateHeader(StaticFileResponseContext ctx) =>
        ctx.Context.Response.Headers[HeaderNames.CacheControl] = AlwaysRevalidate;
}
