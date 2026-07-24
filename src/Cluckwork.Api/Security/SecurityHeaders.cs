namespace Cluckwork.Api.Security;

// #144 — a handful of static response headers, hand-rolled rather than pulling
// in an external security-headers package (the values never vary per request).
// Applied via Response.OnStarting so they land on EVERY response — API, static
// files, the SPA fallback, and error responses re-executed by the exception
// handler — no matter which branch of the pipeline produced it.
public static class SecurityHeaders
{
    // Strict same-origin policy. The SPA loads only same-origin scripts, styles,
    // fonts and images and calls only the same-origin API (/api/v1), so every
    // fetch directive is 'self'; the pre-paint theme script was moved out of
    // index.html into a same-origin file precisely so script-src needs no hash
    // or nonce (#144). frame-ancestors 'none' blocks clickjacking; object-src
    // 'none' and base-uri 'self' close the usual injection escape hatches.
    //
    // img-src carries `blob:` for one reason (#123): the farm logo is served
    // from an endpoint behind the Authorization header, which an <img src> to
    // /api/v1/account/logo cannot send. The SPA fetches the bytes through the
    // API client and renders them from an object URL, and `blob:` is what lets
    // that <img> paint. It widens img-src only to URLs this document itself
    // minted — a blob: URL is same-origin, opaque and unguessable, and cannot
    // be pointed at a remote host — so it does not reopen an exfiltration
    // channel the way adding a scheme like https: would.
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' blob:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-src 'none'; " +
        "worker-src 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var headers = ((HttpContext)state).Response.Headers;
                // Don't clobber a header a downstream handler set deliberately.
                headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy);
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("Referrer-Policy", "no-referrer");
                // Redundant with frame-ancestors 'none' for modern browsers, kept
                // for older ones that predate CSP frame-ancestors.
                headers.TryAdd("X-Frame-Options", "DENY");
                return Task.CompletedTask;
            }, context);
            await next();
        });
}
