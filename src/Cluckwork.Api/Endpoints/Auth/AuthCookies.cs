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
    // #532/#547 — the refresh cookie is named PER FARM. One shared cookie made a
    // browser with two farms open fight over a single slot: Login, Refresh and
    // ChangePassword all wrote it, so whichever acted last owned every tab's
    // session. Four P1s across rounds 6-9 were all instances of that one race,
    // and each fix created the next.
    //
    // Per-farm names remove the race instead of detecting it. The browser sends
    // every cookie matching the path, so several may arrive; the caller names
    // which farm it wants (ExpectedAccountHeaderName) and we read only that one.
    // Reading another farm's cookie is therefore not something we guard against
    // — it is something the caller cannot express.
    //
    // "N" format: 32 hex chars, no dashes. Cookie names permit it; dashes would
    // too, but N keeps the name short and matches how account ids are logged.
    public static string RefreshCookieNameFor(Guid accountId) => $"cluckwork_rt_{accountId:N}";

    // Pre-per-farm sessions carry this name. Read-only, and accepted only by
    // Refresh's migration path and Logout — never written. See PART D.
    public const string LegacyRefreshCookieName = "cluckwork_rt";

    public const string CsrfHeaderName = "X-Cluckwork-Auth";

    // #547 — the tab tells us which farm it believes it is refreshing. Since the
    // #532 per-farm rename it is the SELECTOR for which cookie to read: present
    // and parseable means read ONLY that farm's cookie, absent means the
    // bootstrap path, present but unparseable is refused (fail closed).
    //
    // A header, not a body field: refresh takes no body — the handler drains it
    // to enforce the #309 size cap — and adding one would change that contract.
    //
    // OPTIONAL by design: the load-time bootstrap runs before any tab knows its
    // farm. Absent means "no expectation", which is not the same as a mismatch.
    public const string ExpectedAccountHeaderName = "X-Cluckwork-Account";

    // Scoped so the cookie rides only the auth endpoints, never every API call.
    private const string CookiePath = "/api/v1/auth";

    public static void SetRefreshCookie(HttpResponse response, Guid accountId, string refreshToken, int lifetimeDays, bool secure) =>
        response.Cookies.Append(RefreshCookieNameFor(accountId), refreshToken,
            BuildOptions(secure, o => o.Expires = DateTimeOffset.UtcNow.AddDays(lifetimeDays)));

    // Delete with the SAME attributes (path/secure/samesite) so the browser
    // matches and removes the cookie rather than leaving a stale one behind.
    // Targets ONE farm's cookie — the per-farm name is what keeps a logout or a
    // failed refresh from ever touching another farm's session (#532).
    public static void ClearRefreshCookie(HttpResponse response, Guid accountId, bool secure) =>
        response.Cookies.Delete(RefreshCookieNameFor(accountId), BuildOptions(secure, _ => { }));

    public static string? ReadRefreshCookie(HttpRequest request, Guid accountId) =>
        ReadCookie(request, RefreshCookieNameFor(accountId));

    // #532 — the pre-per-farm cookie, read-only. Refresh's migration branch
    // accepts it, and Logout must revoke it; no endpoint writes this name again.
    public static string? ReadLegacyRefreshCookie(HttpRequest request) =>
        ReadCookie(request, LegacyRefreshCookieName);

    // Every cookie read goes through here: ASP.NET's cookie parser does NOT
    // percent-decode values (it splits name=value at the first '='), so the
    // value arrives exactly as the browser sent it — and the framework's
    // Set-Cookie writer percent-encodes the refresh token when it is written,
    // which is the form the browser resends. The stored hash was computed over
    // the RAW token, so without this single decode every browser-shaped
    // request would miss its own session.
    private static string? ReadCookie(HttpRequest request, string name)
    {
        if (!request.Cookies.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
            return null;
        return Uri.UnescapeDataString(value);
    }

    // Delete the legacy cookie with the SAME attributes as every other auth
    // cookie so the browser matches and removes it. Refresh uses this while
    // migrating the session; Logout uses it while ending the old session.
    public static void ClearLegacyRefreshCookie(HttpResponse response, bool secure) =>
        response.Cookies.Delete(LegacyRefreshCookieName, BuildOptions(secure, _ => { }));

    // Every farm this browser holds a session for, on this path. Used by the
    // bootstrap path when the caller has not yet told us which farm it wants.
    public static IReadOnlyList<Guid> RefreshCookieAccounts(HttpRequest request)
    {
        var accounts = new List<Guid>();
        foreach (var name in request.Cookies.Keys)
        {
            if (!name.StartsWith("cluckwork_rt_", StringComparison.Ordinal)) continue;
            if (Guid.TryParseExact(name["cluckwork_rt_".Length..], "N", out var accountId))
                accounts.Add(accountId);
        }
        return accounts;
    }

    public static bool HasCsrfHeader(HttpRequest request) =>
        request.Headers.ContainsKey(CsrfHeaderName);

    private static CookieOptions BuildOptions(bool secure, Action<CookieOptions> extra)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            // Secure in every non-Development environment so a misconfigured
            // forwarded-proto can never emit a non-Secure auth cookie in prod; off
            // only for plain-HTTP local dev so the browser will still store it.
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            IsEssential = true, // an auth cookie is not subject to consent gating
        };
        extra(options);
        return options;
    }
}
