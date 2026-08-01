namespace Cluckwork.Api.Hosting;

using Microsoft.Net.Http.Headers;

// #312 — origin-side safe cache default. The app previously emitted no
// Cache-Control on API responses at all, so a browser cache, a misconfigured
// intermediary, or a future edge rule could retain tenant data (JSON bodies,
// CSV/zip exports, even auth responses carrying session material). Hand-rolled
// the same way as SecurityHeaders (#144) rather than reaching for
// [ResponseCache]/OutputCaching: this app is minimal-API only (AGENTS.md — "no
// MediatR", same spirit applies to MVC-only filters), and [ResponseCache] is an
// MVC filter-pipeline attribute that minimal-API endpoints never run — it would
// need to be remembered on every new MapGroup, exactly the per-endpoint
// forgetting this issue exists to close. OutputCaching/ResponseCaching solve
// the opposite problem (opting endpoints INTO caching); neither gives a
// deny-by-default that reaches every response with zero per-endpoint wiring. A
// single OnStarting callback, registered outermost like SecurityHeaders,
// reaches every response — reads, writes, auth, validation, errors, exports —
// with nothing to opt into.
//
// TryAdd (not direct assignment), so it never clobbers a header a downstream
// stage set deliberately: static files (StaticAssetCaching.ApplyCacheHeaders),
// the SPA fallback (StaticAssetCaching.AlwaysRevalidateHeader), and the farm
// logo endpoint's own ETag-revalidate policy all assign Cache-Control directly,
// which happens before this callback fires regardless of registration order —
// direct assignment happens inline while the response is being produced,
// while OnStarting callbacks fire only once the response actually starts
// sending. /health is excluded, mirroring the existing carve-outs in
// Program.cs (the Serilog level selector and the /health/{**rest} catch-all),
// so a probe's own caching contract is never masked by this default.
public static class DefaultResponseCaching
{
    public const string NoStore = "private, no-store";

    private const string HealthPathPrefix = "/health";

    public static IApplicationBuilder UseDefaultResponseCaching(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(HealthPathPrefix, StringComparison.Ordinal))
            {
                context.Response.OnStarting(static state =>
                {
                    ((HttpContext)state).Response.Headers.TryAdd(HeaderNames.CacheControl, NoStore);
                    return Task.CompletedTask;
                }, context);
            }
            await next();
        });
}
