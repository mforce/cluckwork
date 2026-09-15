namespace Cluckwork.Api.Security;

using System.Security.Cryptography;

// #144 — a handful of response headers, hand-rolled rather than pulling in an
// external security-headers package. Applied via Response.OnStarting so they
// land on EVERY response — API, static files, the SPA shell, and error
// responses re-executed by the exception handler — no matter which branch of
// the pipeline produced it.
//
// Every value here is constant except one: #873 gives style-src a per-response
// nonce, so the policy is BUILT rather than stored. An earlier version of this
// comment said "the values never vary per request", which is now false.
public static class SecurityHeaders
{
    // 128 bits from a cryptographic RNG. CSP asks only that a nonce be
    // unguessable; base64 of 16 bytes is 24 characters, short enough to repeat
    // in a header and in the document without bloating either.
    public const int NonceByteCount = 16;

    // The <meta> the SPA shell writes the same nonce into, and the only place
    // web/src/theme/FarmThemeProvider.tsx reads it from.
    public const string NonceMetaName = "csp-nonce";

    private const string NonceItemKey = "Cluckwork.Csp.Nonce";

    // Strict same-origin policy. The SPA loads only same-origin scripts, styles,
    // fonts and images and calls only the same-origin API (/api/v1), so every
    // fetch directive is 'self'; the pre-paint theme script was moved out of
    // index.html into a same-origin file precisely so script-src needs no hash
    // or nonce (#144). frame-ancestors 'none' blocks clickjacking; object-src
    // 'none' and base-uri 'self' close the usual injection escape hatches.
    //
    // style-src keeps 'self' AND carries a per-response nonce (#873). Both
    // halves are load-bearing. 'self' admits styles.css and the Inter font CSS,
    // which are same-origin <link> elements a nonce does not cover. The nonce
    // admits the <style> elements Emotion injects at runtime for MUI (#674) —
    // without it the browser drops every one of them, so no `sx`, `styled()` or
    // `styleOverrides` value reaches the screen, and it fails silently. The
    // policy stays strict: 'unsafe-inline' was rejected in #873 because it
    // reopens CSS injection, including attribute-selector exfiltration.
    // script-src deliberately gains nothing here — scripts are still external
    // same-origin files, so they need no nonce and must not be given one.
    //
    // img-src carries `blob:` for one reason (#123): the farm logo is served
    // from an endpoint behind the Authorization header, which an <img src> to
    // /api/v1/account/logo cannot send. The SPA fetches the bytes through the
    // API client and renders them from an object URL, and `blob:` is what lets
    // that <img> paint. It widens img-src only to URLs this document itself
    // minted — a blob: URL is same-origin, opaque and unguessable, and cannot
    // be pointed at a remote host — so it does not reopen an exfiltration
    // channel the way adding a scheme like https: would.
    //
    // worker-src is 'self' rather than 'none' because the PWA service worker
    // (#142) is a same-origin script at /sw.js, and 'none' blocks registration
    // outright — silently, since a blocked register() only rejects a promise.
    // It stays 'self': a worker may still only be loaded from this origin, so
    // this permits our own shell-caching worker and nothing remote.
    public static string BuildContentSecurityPolicy(string styleNonce) =>
        "default-src 'self'; " +
        "script-src 'self'; " +
        $"style-src 'self' 'nonce-{styleNonce}'; " +
        "img-src 'self' blob:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-src 'none'; " +
        "worker-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    /// <summary>The style nonce for this response, minted on first ask and reused after.</summary>
    /// <remarks>
    /// The SPA shell asks for it while building the document it is about to
    /// return and the header callback asks for it on the way out, so the header
    /// and the page carry the same value by construction rather than by two
    /// independent draws that could disagree. Never logged, and never put where
    /// a log scope or an exception message would pick it up.
    /// </remarks>
    public static string GetOrCreateNonce(HttpContext context)
    {
        if (context.Items.TryGetValue(NonceItemKey, out var existing) && existing is string already)
            return already;

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceByteCount));
        context.Items[NonceItemKey] = nonce;
        return nonce;
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var current = (HttpContext)state;
                var headers = current.Response.Headers;
                // Don't clobber a header a downstream handler set deliberately.
                headers.TryAdd("Content-Security-Policy",
                    BuildContentSecurityPolicy(GetOrCreateNonce(current)));
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
