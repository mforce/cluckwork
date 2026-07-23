namespace Cluckwork.Api.Endpoints.Auth;

// #145 — the refresh token lives only in an HttpOnly cookie, never in a response
// body or JS-readable storage; the access token stays a Bearer header held in
// client memory. CSRF posture: SameSite=Strict keeps the cookie off cross-site
// requests, and refresh/logout additionally require a custom header a cross-site
// simple request cannot set (adding it forces a CORS preflight the API doesn't
// grant). All other write endpoints already require Authorization + Idempotency
// headers, which cross-site forms likewise cannot set.
public static class AuthCookies
{
    public const string RefreshCookieName = "cluckwork_rt";
    public const string CsrfHeaderName = "X-Cluckwork-Auth";

    // Scoped so the cookie rides only the auth endpoints, never every API call.
    private const string CookiePath = "/api/v1/auth";

    public static void SetRefreshCookie(HttpResponse response, string refreshToken, int lifetimeDays) =>
        response.Cookies.Append(RefreshCookieName, refreshToken,
            BuildOptions(response.HttpContext, o => o.Expires = DateTimeOffset.UtcNow.AddDays(lifetimeDays)));

    // Delete with the SAME attributes (path/secure/samesite) so the browser
    // matches and removes the cookie rather than leaving a stale one behind.
    public static void ClearRefreshCookie(HttpResponse response) =>
        response.Cookies.Delete(RefreshCookieName, BuildOptions(response.HttpContext, _ => { }));

    public static string? ReadRefreshCookie(HttpRequest request) =>
        request.Cookies.TryGetValue(RefreshCookieName, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    public static bool HasCsrfHeader(HttpRequest request) =>
        request.Headers.ContainsKey(CsrfHeaderName);

    private static CookieOptions BuildOptions(HttpContext context, Action<CookieOptions> extra)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            // HTTPS in production (the forwarded proto from #144 makes IsHttps
            // reflect the real client scheme); off for plain-HTTP local dev so the
            // browser will still store it.
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            IsEssential = true, // an auth cookie is not subject to consent gating
        };
        extra(options);
        return options;
    }
}
