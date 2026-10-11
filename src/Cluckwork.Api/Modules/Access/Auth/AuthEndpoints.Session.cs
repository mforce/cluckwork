using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Api.Modules.Access.Auth;

public static partial class AuthEndpoints
{
    // #309 — the refresh token is a 256-bit value rendered as standard Base64
    // (Convert.ToBase64String — not URL-safe, so it can include +, / and =
    // padding), which is exactly 44 chars for 32 bytes (IdentityProvider's
    // GenerateRefreshToken); 512 is a generous ceiling that still rejects a
    // padded/garbage cookie value before it reaches the token store.
    private const int MaxRefreshTokenLength = 512;

    // #532 — several per-farm refresh cookies are present and the caller has not
    // named one (the bootstrap path with the header absent). Distinct from a
    // dead session: nothing is rotated and nothing is cleared, so no tab is
    // harmed — the SPA treats it as "go to login" for now; the farm picker that
    // makes the code self-service is #535's job.
    public const string FarmSelectionRequiredCode = "Auth.FarmSelectionRequired";

    private static async Task<IResult> Refresh(
        HttpRequest request, HttpResponse response, IAccessModule access,
        IWebHostEnvironment env, CancellationToken ct)
    {
        // #309 — refresh carries no bound body parameter (the token rides in the
        // cookie), so the middleware's byte-capped stream is only enforced if
        // something actually reads Request.Body — for every OTHER auth endpoint,
        // JSON parameter binding does that automatically; refresh has none.
        // Actively drain (and discard) the body here so an oversized/chunked body
        // throws (413) instead of silently passing through unread. Reproduced
        // live: without this, a multi-MB chunked body reaches this handler
        // untouched. Drained first (before the CSRF header check) to reject the
        // cheapest attack first; the throw propagates normally up through the
        // pipeline to UseExceptionHandler/`/error` (this is a handler-thrown
        // exception, not one swallowed inside framework JSON-binding code), which
        // already maps BadHttpRequestException to a 413 ProblemDetails.
        await request.Body.CopyToAsync(Stream.Null, ct);

        // CSRF: SameSite=Strict already keeps the cookie off cross-site requests;
        // the custom header (which a cross-site simple request can't set) is the
        // belt-and-braces second check.
        if (!AuthCookies.HasCsrfHeader(request))
            return Results.Problem("Missing required header.", statusCode: 403, title: "Auth.CsrfHeaderRequired");

        // #532 — the header selects WHICH cookie to read. Present and
        // parseable: read only that farm's cookie. Absent: the bootstrap path
        // below (the tab does not know its farm yet). Present but unparseable:
        // refuse, read nothing — fail closed, as before: a client that thinks
        // it knows its farm and got it wrong must not fall through to the
        // broader bootstrap path.
        Guid? accountId = null;
        if (request.Headers.TryGetValue(AuthCookies.ExpectedAccountHeaderName, out var expectedValue))
        {
            if (!Guid.TryParse(expectedValue.ToString(), out var parsedAccountId))
                return Results.Problem("Not authenticated.", statusCode: 401, title: "Auth.SessionChanged");
            accountId = parsedAccountId;
        }
        else
        {
            // Bootstrap: the page loaded and the tab has no access token yet,
            // so it does not know its farm. Exactly one per-farm cookie is the
            // overwhelmingly common case (one farm per browser) and keeps
            // working with no user-visible change. Several means do not guess —
            // a distinct code, no rotation, nothing cleared; the SPA sends the
            // user to login, whose farm-code field handles it (the picker is
            // #535's job). None: try the one-way legacy upgrade below before
            // returning the ordinary no-session 401.
            var candidates = AuthCookies.RefreshCookieAccounts(request);
            if (candidates.Count > 1)
                return Results.Problem(
                    "This browser holds sessions for several farms; choose one.",
                    statusCode: 401, title: FarmSelectionRequiredCode);
            if (candidates.Count == 1)
                accountId = candidates[0];
        }

        var refreshToken = accountId is { } selectedAccount
            ? AuthCookies.ReadRefreshCookie(request, selectedAccount)
            : null;
        if (refreshToken is null)
        {
            // #532 — the one-way legacy upgrade: a pre-per-farm session presents
            // the shared name, which NO endpoint but this one accepts (Login and
            // change-password always write the new per-farm form). Rotate it as
            // normal, then write the new per-farm cookie AND delete the legacy
            // one in the SAME response — the browser ends up with exactly one
            // cookie for this farm. The legacy name is never written, so the
            // branch drains naturally within one refresh-token lifetime.
            // Removal condition: this branch can be deleted once
            // Jwt:RefreshTokenDays (30) has elapsed since deploy; #537 should
            // record that.
            //
            // The token value is opaque, so the stored token's AccountId —
            // returned on the minted TokenPair — tells a headerless bootstrap
            // which per-farm name to write. If a caller supplied an expected
            // farm, RefreshAsync also checks it before rotating; a legacy token
            // for another farm therefore fails closed and remains untouched.
            var legacy = AuthCookies.ReadLegacyRefreshCookie(request);
            if (legacy is not null && legacy.Length <= MaxRefreshTokenLength)
            {
                var legacyResult = await access.RefreshAsync(legacy, accountId, ct);
                if (legacyResult.IsSuccess)
                    return LegacyUpgradeResult(response, legacyResult.Value, access.RefreshTokenLifetimeDays, CookieSecure(env));
                // A dead legacy cookie (rotated elsewhere / expired) is not
                // this farm's to clear on the legacy name — it is not per-farm.
                // Leave it; it is inert. Fall through to the no-cookie 401.
            }

            // A named farm with no matching cookie is a changed session. With
            // no selector and no legacy/per-farm cookie, this is the ordinary
            // headerless no-session bootstrap response. Neither clears a
            // cookie: there is no selected live credential to touch.
            return Results.Problem(
                "Not authenticated.", statusCode: 401,
                title: accountId is null ? "Identity.InvalidRefreshToken" : "Auth.SessionChanged");
        }

        var selectedAccountId = accountId
            ?? throw new InvalidOperationException("A per-farm refresh cookie requires an account selector.");

        // #309 — a real refresh token is a fixed, short base64 string. Treat an
        // over-length cookie value like a MISSING cookie (clear + the same
        // "Not authenticated." 401 as the branch just above) rather than like a
        // genuine-but-rejected token from RefreshAsync below (which returns
        // result.Error.Description, a different string) — both are equally
        // non-enumerating, this just matches the response actually produced.
        if (refreshToken.Length > MaxRefreshTokenLength)
        {
            AuthCookies.ClearRefreshCookie(response, selectedAccountId, CookieSecure(env));
            return Results.Problem("Not authenticated.", statusCode: 401, title: "Identity.InvalidRefreshToken");
        }

        var result = await access.RefreshAsync(refreshToken, selectedAccountId, ct);
        if (!result.IsSuccess)
        {
            // The cookie's token is invalid / already rotated / expired — expire
            // it so the browser stops presenting a dead token on every load.
            // The clear can only ever reach the cookie of the farm the caller
            // named (#532): P1-2's "cleared the other farm's cookie" is
            // unrepresentable here, not guarded.
            AuthCookies.ClearRefreshCookie(response, selectedAccountId, CookieSecure(env));
            return Results.Problem(result.Error.Description, statusCode: 401, title: result.Error.Code);
        }

        // #547 — RefreshAsync's expectedAccountId check above is
        // defence-in-depth, not the primary guarantee: the cookie was selected
        // by the caller's own farm name, so the stored token's AccountId should
        // always match it. If it ever does not, something is wrong and we want
        // to fail closed on it.

        AuthCookies.SetRefreshCookie(response, selectedAccountId, result.Value.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }

    // #532 — the legacy upgrade response: the new per-farm cookie AND the legacy
    // delete ride the same response, so the browser ends the exchange holding
    // exactly one refresh cookie for this farm. The legacy name remains
    // read-only; Logout is the only other path that deletes it.
    private static IResult LegacyUpgradeResult(
        HttpResponse response, TokenPair pair, int lifetimeDays, bool secure)
    {
        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, lifetimeDays, secure);
        AuthCookies.ClearLegacyRefreshCookie(response, secure);
        return Results.Ok(new AccessTokenResponse(pair.AccessToken, pair.AccessTokenExpiry));
    }

    // #336 review — logout revokes along TWO independent axes, because the two
    // credentials a logout can present do not necessarily name the same user:
    //
    //   - refresh COOKIES are per-farm; the tab's account header selects one.
    //   - the ACCESS TOKEN is per-tab: web/src/auth/tokenStore.ts keeps it in
    //     that tab's module memory, so a tab logged in as A survives a later
    //     login as B in another tab.
    //
    // The bearer and selected cookie can still name different users: the
    // header is a cookie selector, while the bearer identifies the actor.
    // Deriving the grant owner from the cookie alone can therefore leave the
    // actor's outstanding step-up grant alive. The same gap opens whenever the
    // selected cookie is missing or expired.
    //
    // Hence both, each guarded on its own presence and neither depending on the
    // other:
    //   - cookie present  -> revoke that refresh token AND record its owner's
    //                        logout (the session actually being ended).
    //   - caller authenticated -> record the bearer subject's logout, so their
    //                        grants die even when the cookie names someone else,
    //                        or is absent/expired entirely.
    // Same user via both paths is deliberately safe rather than deduplicated:
    // each durable epoch advance can only over-revoke pre-logout grants.
    //
    // Still ANONYMOUS-capable: the bearer is optional, so a logout with an
    // expired access token keeps working off the cookie alone (#145). The CSRF
    // header stays mandatory either way. /auth/logout is exempt from
    // IdempotencyMiddleware, so the now-possible authenticated call does not
    // start demanding an Idempotency-Key.
    //
    // PR #336 review — the bearer recording runs FIRST, before the cookie
    // revocation, and its own success does not depend on the cookie path at all.
    // Since #338 RecordLogoutAsync advances the durable StepUpLogoutEpoch in
    // Postgres, so it CAN throw, just like RevokeRefreshTokenAsync — no longer the
    // infallible in-memory record it once was. Recording first still wins the case
    // that matters: when the epoch bump SUCCEEDS and the refresh revocation then
    // throws, the grant-kill has already committed, so the failure over-revokes
    // rather than leaving a captured access token + grant usable until the grant's
    // own short expiry. When the bump itself throws (both hit the same database),
    // the whole logout fails loudly and the short-lived grant lapses on its own
    // expiry. The exception is deliberately NOT caught here: a request failing
    // loudly when the DB is down is worth preserving, and the SPA already treats
    // logout as best-effort regardless of status code.
    //
    // #532 — the account header selects this tab's per-farm cookie. When it is
    // absent, the bearer account_id claim is the fallback. With neither, clear
    // no per-farm cookie; logout remains idempotent and never sweeps another
    // farm's named session. The temporary legacy cookie is unambiguous only
    // when it is the sole presented refresh session. Alongside a selected farm,
    // its durable owner must match that farm before it is revoked or cleared.
    // With no selector and any named farm cookie present, infer nothing and
    // preserve every session (RefreshAccountBindingTests, #569/#570).
    private static async Task<IResult> Logout(
        HttpRequest request, HttpResponse response, IAccessModule access,
        ICurrentUser currentUser, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!AuthCookies.HasCsrfHeader(request))
            return Results.Problem("Missing required header.", statusCode: 403, title: "Auth.CsrfHeaderRequired");

        if (currentUser.IsResolved)
            await access.RecordLogoutAsync(currentUser.UserId, ct);

        // The header is checked first, and it wins: it is the tab declaring
        // which farm it is ending. The per-farm rename made that declaration
        // load-bearing for the SPA's own logout (the browser sends every
        // farm's cookie, so the tab must say which one it means), and it is
        // exactly as much of a trust boundary as the bearer's claim — the
        // cookie the named farm holds is what gets revoked, and presenting a
        // farm's cookie is possession of that farm's credential. With the
        // header absent the bearer's account_id claim routes the logout without
        // growing ICurrentUser with cookie-routing state. With neither there is
        // nothing to clear (idempotent 204).
        Guid? accountId = null;
        if (request.Headers.TryGetValue(AuthCookies.ExpectedAccountHeaderName, out var headerValue)
            && Guid.TryParse(headerValue.ToString(), out var parsed))
            accountId = parsed;
        else if (request.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            var claim = request.HttpContext.User.FindFirst("account_id")?.Value;
            if (Guid.TryParse(claim, out var callerAccount))
                accountId = callerAccount;
        }

        var selectedRefreshToken = accountId is { } selectedAccount
            ? AuthCookies.ReadRefreshCookie(request, selectedAccount)
            : null;
        var legacyRefreshToken = AuthCookies.ReadLegacyRefreshCookie(request);
        var legacyIsOnlyPresentedSession = accountId is null
            && legacyRefreshToken is not null
            && AuthCookies.RefreshCookieAccounts(request).Count == 0;
        var legacyMatchesSelected = legacyRefreshToken is not null
            && string.Equals(legacyRefreshToken, selectedRefreshToken, StringComparison.Ordinal);
        var clearLegacyCookie = legacyIsOnlyPresentedSession;

        // Revoke every attributable credential before clearing its browser copy.
        // If the database operation fails, Logout fails loudly and a retry can
        // safely repeat the idempotent revocations. When the same token appears
        // under both names, revoke once and clear both names. A distinct legacy
        // cookie is cleared only after the provider's durable owner lookup says
        // it belongs to the selected farm (RefreshAccountBindingTests, #569).
        if (selectedRefreshToken is not null)
        {
            var selectedOutcome = await access.RevokeRefreshTokenAsync(
                selectedRefreshToken, accountId, ct);
            if (legacyMatchesSelected)
                clearLegacyCookie = selectedOutcome == RefreshTokenRevocationOutcome.InScope;
        }
        if (legacyRefreshToken is not null && !legacyMatchesSelected)
        {
            if (legacyIsOnlyPresentedSession)
                await access.RevokeRefreshTokenAsync(legacyRefreshToken, null, ct);
            else if (accountId is not null)
            {
                var legacyOutcome = await access.RevokeRefreshTokenAsync(legacyRefreshToken, accountId, ct);
                clearLegacyCookie = legacyOutcome == RefreshTokenRevocationOutcome.InScope;
            }
        }

        var secure = CookieSecure(env);
        if (accountId is { } account)
            AuthCookies.ClearRefreshCookie(response, account, secure);
        if (clearLegacyCookie)
            AuthCookies.ClearLegacyRefreshCookie(response, secure);
        return Results.NoContent();
    }
}
