namespace Cluckwork.Api.Endpoints.Auth;

using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Api.Validation;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Application.Features.Users.ChangeOwnPassword;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.Extensions.Options;

public static class AuthEndpoints
{
    // #309 — the refresh token is a 256-bit value rendered as standard Base64
    // (Convert.ToBase64String — not URL-safe, so it can include +, / and =
    // padding), which is exactly 44 chars for 32 bytes (IdentityProvider's
    // GenerateRefreshToken); 512 is a generous ceiling that still rejects a
    // padded/garbage cookie value before it reaches the token store.
    private const int MaxRefreshTokenLength = 512;

    // #308 — the step-up grant rides as its own header, never the request
    // body: it is proof-of-recent-auth, not domain data, so keeping it out of
    // the JSON body means it can never accidentally get folded into a payload
    // dump/log. Mirrors AuthCookies.CsrfHeaderName's rationale.
    public const string StepUpHeaderName = "X-Cluckwork-Step-Up";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        // Strict limit — login is the password-spraying target (#143). On success
        // the refresh token is set as an HttpOnly cookie (#145); the body carries
        // only the access token.
        group.MapPost("/login", Login)
            .AllowAnonymous()
            // #532 — AllowAnonymous governs AUTHORIZATION and leaves an
            // ambient bearer's principal in place. This marker is what makes
            // AmbientPrincipalMiddleware blank it, so a farm-A token sent to a
            // farm-B login cannot resolve farm A as the tenant and thereby
            // bypass the #128 lockout on farm B's user. See the middleware.
            .WithMetadata(new IgnoresAmbientPrincipalAttribute())
            .RequireRateLimiting(RateLimitingOptions.LoginPolicyName)
            // #309 — 4 KB, not 2 KB: System.Text.Json's DEFAULT encoder escapes
            // non-ASCII as 6-byte \uXXXX sequences, so a maximum-length (256-char)
            // email + password serialized via HttpClient.PostAsJsonAsync (this
            // repo's own integration tests use it) can reach ~3.1 KB — a 2 KB cap
            // rejected a legitimate maximum-length credential. An oversized body
            // is still 413'd before binding / the PBKDF2 verify (incl. the
            // unknown-user equalization hash).
            .WithMaxRequestBodyBytes(4096)
            .WithName("Login")
            .WithSummary("Exchange credentials for an access token; sets the refresh-token cookie.");

        // Looser limit — refresh guards a high-entropy token AND carries
        // legitimate automatic session traffic, so it must not share login's
        // budget (several users behind one NAT IP would starve it). The refresh
        // token comes from the cookie, never the body (#145).
        group.MapPost("/refresh", Refresh)
            .AllowAnonymous()
            // #532 — same reason as /login. A bearer sent here still populates
            // context.User, so TenantResolutionMiddleware resolves THAT farm
            // while RefreshAsync rotates a cookie belonging to another: the
            // rotation is a tracked SaveChanges, so TenantStampInterceptor
            // refuses it and the caller gets a 500 instead of a rotation or a
            // clean 401. Isolation holds — the write is refused — but the 500 is
            // reachable with a hand-crafted request.
            .WithMetadata(new IgnoresAmbientPrincipalAttribute())
            .RequireRateLimiting(RateLimitingOptions.RefreshPolicyName)
            // #309 — refresh carries no body of its own (the token rides in the
            // cookie); 1 KB is a defensive cap so a junk body can't be streamed in.
            .WithMaxRequestBodyBytes(1024)
            // #398 review round 8 — the handler DRAINS Request.Body to enforce
            // that cap (see Refresh), so Kestrel can raise a 400 here for a
            // truncated/malformed body. It declares no typed body parameter, so
            // without this marker that failure would be reported as `errors.query`
            // on an endpoint with no query input at all. BodyReadingEndpointTests
            // is what keeps this from being forgotten again.
            .WithMetadata(new ReadsRequestBodyAttribute())
            .WithName("RefreshToken")
            .WithSummary("Rotate the refresh-token cookie and return a fresh access token.");

        // Anonymous + cookie-authenticated (like refresh): logout is proven by the
        // HttpOnly refresh cookie plus the CSRF header, so it works even with an
        // expired access token. It must always be able to destroy the session
        // (#145 review).
        //
        // #336 review — the SPA now ALSO sends its bearer when it still has one,
        // so the handler can revoke the access-token user's step-up grants and
        // not just the cookie owner's (see Logout). AllowAnonymous is what keeps
        // that additive: a missing or expired bearer simply leaves the caller
        // unauthenticated instead of failing the request. An authenticated
        // logout does resolve a tenant, which would normally make
        // IdempotencyMiddleware demand an Idempotency-Key — /auth/logout is
        // exempted there precisely so this stays a header-free call.
        group.MapPost("/logout", Logout)
            .AllowAnonymous()
            .WithName("Logout")
            .WithSummary("Revoke the refresh token and expire its cookie.");

        // #165 — self-service password change, for EVERY role (so it can't live in
        // the Owner-only users group). Lives here because the response rotates the
        // refresh cookie, which is path-scoped to /api/v1/auth. It verifies a
        // credential, so it carries the login rate limit: an attacker holding a
        // stolen access token must not be able to brute-force the current password
        // and take the account over.
        group.MapPost("/change-password", ChangePassword)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitingOptions.LoginPolicyName)
            // #309 — 4 KB, not 2 KB: two maximum-length (256-char) passwords
            // escaped by System.Text.Json's default \uXXXX encoder (see the
            // login comment above) measured at ~3.1 KB; 4 KB still caps the body
            // ahead of binding and the current-password PBKDF2 verify.
            .WithMaxRequestBodyBytes(4096)
            .WithName("ChangeOwnPassword")
            .WithSummary("Change your own password; signs out other devices.");

        // #308 — re-confirm the CURRENT password to mint a short-lived,
        // audience-limited step-up grant for a sensitive user-administration
        // operation (see StepUpGrantService for the full threat model). Open
        // to any authenticated role (the three consumers — CreateUser,
        // SetUserPassword, ChangeUserRole (#355) — are already Owner-only), so
        // the endpoint stays a generic building block. Carries the login rate
        // limit for the same reason change-password does: it verifies a
        // credential, so an attacker holding a stolen access token must not
        // get unlimited password-guessing attempts.
        group.MapPost("/step-up", StepUp)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitingOptions.LoginPolicyName)
            // A single ≤256-char password; mirrors change-password's 4 KB cap
            // (System.Text.Json's \uXXXX escaping of non-ASCII, #309).
            .WithMaxRequestBodyBytes(4096)
            .WithName("StepUp")
            .WithSummary("Re-confirm the current password; returns a short-lived step-up grant.");

        return group;
    }

    // #532 — several per-farm refresh cookies are present and the caller has not
    // named one (the bootstrap path with the header absent). Distinct from a
    // dead session: nothing is rotated and nothing is cleared, so no tab is
    // harmed — the SPA treats it as "go to login" for now; the farm picker that
    // makes the code self-service is #535's job.
    public const string FarmSelectionRequiredCode = "Auth.FarmSelectionRequired";

    // #283 follow-up (#361) — the error code a failed sign-in carries when the
    // DEFAULT ACCOUNT has no Owner, so the SPA can explain the dead end instead
    // of showing the generic denial. Rides the ProblemDetails `title`, like
    // every other login error code. Scoped to that account, not to the instance
    // — see the Login handler for what that does and does not claim.
    public const string NoOwnerProvisionedCode = "Auth.NoOwnerProvisioned";

    // #532 — the farm code names a farm this deployment does not have. Epic #530
    // decision 6 chose a DISTINCT response knowingly: it makes /auth/login a
    // farm-enumeration oracle, and that was accepted because the alternative
    // (a generic denial) leaves an operator who mistyped their farm code with no
    // way to tell that from a wrong password. The slug MUST NOT reach a metric
    // label or a log dimension — it is attacker-supplied and unbounded.
    public const string UnknownFarmCodeCode = "Auth.UnknownFarmCode";

    // #532 — the farm exists and is suspended. DISTINCT from invalid credentials
    // by owner decision, AMENDING epic #530 decision 6, which required suspended
    // and active farms to be indistinguishable. The trade was made explicitly:
    // telling a farm's own staff their password is wrong is a lie that generates
    // support load, and farm existence is already disclosed by the branch above.
    public const string FarmSuspendedCode = "Auth.FarmSuspended";

    // In every environment but Development the browser reaches the app over HTTPS
    // (TLS terminates at the proxy), so the auth cookie must be Secure regardless
    // of the internal proxy→app hop scheme.
    private static bool CookieSecure(IWebHostEnvironment env) => !env.IsDevelopment();

    private static async Task<IResult> Login(
        LoginRequest request, IIdentityProvider identity, IValidator<LoginRequest> validator,
        HttpResponse response, IOptions<JwtOptions> jwt, IWebHostEnvironment env,
        FirstRunStatusService firstRun, IAccountRepository accounts,
        AuthSecurityEventLogger securityEvents, CancellationToken ct)
    {
        // #309 — reject an OVERSIZED email/password (400) before the hasher. An
        // empty/short credential is NOT rejected here: it still flows to
        // LoginAsync and returns the generic non-enumerating 401 (unchanged).
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        // #532 — resolve the FARM before the credential. FindBySlugAsync folds the
        // code to lowercase and ignores the tenant query filter; both are
        // mandatory, not tidiness. Login is AllowAnonymous, so TenantContext is
        // unresolved and the Account filter (AccountId == Guid.Empty) matches
        // zero rows — a lookup written the obvious way reports every farm code as
        // unknown, silently.
        var account = await accounts.FindBySlugAsync(request.FarmCode, ct);
        if (account is null)
        {
            // Identity-free, exactly like every other unsuccessful branch, and
            // carrying NO slug: this stream must not become the enumeration
            // oracle the response already is, at unbounded log cardinality.
            // Deliberately does NOT touch AccessFailedAsync — there is no user
            // row here, and a farm code must never burn a real account's
            // lockout budget.
            securityEvents.LoginFailed();
            return Results.Problem(
                "That farm code is not recognised.",
                statusCode: 401, title: UnknownFarmCodeCode);
        }

        if (!account.IsActive)
        {
            // BEFORE the credential check and before the first-run notice below:
            // a suspended farm must answer the same way whether or not the
            // password was right, and must not fall through to a branch that
            // discloses its provisioning state instead.
            securityEvents.LoginFailed();
            return Results.Problem(
                "This farm is suspended. Contact your administrator.",
                statusCode: 401, title: FarmSuspendedCode);
        }

        var result = await identity.LoginAsync(account.Id, request.Email, request.Password, ct);
        if (!result.IsSuccess)
        {
            // #283 follow-up (#361) — first-run discoverability, reported HERE
            // rather than from an endpoint the login screen polls. A freshly
            // migrated instance has base reference data but no administrator,
            // because no credential is ever migration-baked, so there is nobody
            // for the operator to sign in as and the form used to say nothing
            // about why.
            //
            // Deliberately on the FAILURE path only, and only while the DEFAULT
            // ACCOUNT has no Owner — that account and no other, which is the
            // scope every sentence below is held to:
            //
            //  * It cannot ENUMERATE. The condition is a property of the default
            //    account and never of the address that was typed, so no attempt
            //    reveals anything about any particular account. Once the default
            //    account has an Owner this branch is unreachable and the response
            //    is byte-identical to the generic denial it has always been.
            //    Scoped to the default account, NOT to "any Owner anywhere": an
            //    Owner under a different account leaves this reachable, which
            //    FirstRunLoginNoticeTests sets up and asserts deliberately (it
            //    pins the AccountId predicate), so the wider claim would have
            //    been false in a state this suite already exercises — round 5
            //    caught it there.
            //  * It DOES disclose one fact to an anonymous caller who attempts a
            //    sign-in: the default account has no Owner. Stated plainly
            //    because an earlier version of this comment claimed the
            //    condition "can only hold while no credential exists to protect",
            //    which is false (PR #363 review round 2) — the predicate is the
            //    absence of an OWNER, and the seeders create Workers/Managers
            //    without one, so real protected credentials can exist while this
            //    fires. Accepted: the fact is not itself a credential and grants
            //    no access, it is already inferable by anyone who can reach the
            //    form on a fresh install, and unlike the status endpoint this
            //    replaced it reaches only someone who actually attempted to sign
            //    in. The cost of withholding it is an operator stranded at a
            //    form no credential of theirs can satisfy.
            //  * Only someone actually attempting to sign in learns it — unlike
            //    a status endpoint, which answers anyone who asks without them
            //    trying.
            //  * The cost rides on a request that is already rate limited (#143)
            //    and already ran a PBKDF2 verify, so one indexed EXISTS is
            //    noise; and past first provisioning FirstRunProvisioningLatch
            //    makes it a memory read, so the steady state adds no query at
            //    all.
            //
            // A successful sign-in never reaches this, which is what keeps the
            // check off the hot path.
            // #532 (owner decision, AMENDING this issue's body, which asked for a
            // per-account hint). Kept scoped to the DEFAULT account: making it
            // per-account would answer "does farm X have an Owner yet?" for any
            // farm code an attacker can guess, and would re-open the DoS the
            // latch closed, since a farm that is never provisioned never latches
            // and every failed login re-runs the triple-nested query.
            if (account.Id == SeedDefaults.AccountId && !await firstRun.IsProvisionedAsync(ct))
                // Says ADMINISTRATOR, not "no accounts" and not "no sign-in can
                // succeed" (PR #363 review). The predicate is specifically "the
                // default account has no Owner", which is #283's provisioning
                // invariant — and a non-Owner user can exist without one (the
                // seeders create them, and this suite's own
                // ASuccessfulSignIn_NeverReportsIt sets up exactly that state).
                // Such a user signs in perfectly well, so the broader claim
                // would be false in a reachable state.
                return Results.Problem(
                    "This farm has no administrator account yet — first-time setup has not been "
                    + "completed. Whoever set up this server must create the first administrator.",
                    statusCode: 401,
                    title: NoOwnerProvisionedCode);

            return Results.Problem(result.Error.Description, statusCode: 401, title: result.Error.Code);
        }

        // #532 — per-farm cookie: this login writes the cookie for the farm
        // that was just authenticated and can no longer disturb any other
        // farm's cookie. The old last-login-wins-over-every-tab behaviour is
        // gone by construction, not by a guard.
        AuthCookies.SetRefreshCookie(response, account.Id, result.Value.RefreshToken, jwt.Value.RefreshTokenDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }

    private static async Task<IResult> Refresh(
        HttpRequest request, HttpResponse response, IIdentityProvider identity,
        IOptions<JwtOptions> jwt, IWebHostEnvironment env, CancellationToken ct)
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
                var legacyResult = await identity.RefreshAsync(legacy, ct, accountId);
                if (legacyResult.IsSuccess)
                    return LegacyUpgradeResult(response, legacyResult.Value, jwt.Value.RefreshTokenDays, CookieSecure(env));
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

        var result = await identity.RefreshAsync(refreshToken, ct, selectedAccountId);
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

        AuthCookies.SetRefreshCookie(response, selectedAccountId, result.Value.RefreshToken, jwt.Value.RefreshTokenDays, CookieSecure(env));
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

    private static async Task<IResult> ChangePassword(
        ChangeOwnPasswordRequest request, ChangeOwnPasswordHandler handler,
        IValidator<ChangeOwnPasswordCommand> validator, ICurrentUser currentUser,
        HttpResponse response, IOptions<JwtOptions> jwt, IWebHostEnvironment env,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved) return Results.Unauthorized();

        var command = new ChangeOwnPasswordCommand(request.CurrentPassword, request.NewPassword);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        // The user id comes from the token, never the request: a caller can only
        // ever change their OWN password here.
        var result = await handler.HandleAsync(command, currentUser.UserId, ct);
        if (!result.IsSuccess)
            return Results.Problem(result.Error.Description, statusCode: 400, title: result.Error.Code);

        // Every prior session was revoked; hand this device a fresh cookie + access
        // token so the one that made the change stays signed in.
        // #532 — the graph found this endpoint rewrote the shared cookie with no
        // account expectation at all; it was never part of #547. It now writes
        // the cookie for the CURRENT USER's account — the only farm it can
        // affect — so a change-password in farm A can no longer overwrite
        // farm B's cookie. The account is the handler's own return value
        // (ChangeOwnPasswordHandler returns it), not an ICurrentUser property:
        // the interface is the authorization input, and adding account
        // resolution to it would widen that surface for no gain.
        AuthCookies.SetRefreshCookie(response, result.Value.AccountId, result.Value.RefreshToken, jwt.Value.RefreshTokenDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }

    // #308 — mints the step-up grant. Non-enumerating on failure: a wrong
    // password is fine to say plainly (the caller is already proven to be a
    // specific authenticated user re-confirming their OWN credential — see
    // the class-level threat model on StepUpGrantService).
    private static async Task<IResult> StepUp(
        StepUpRequest request, IStepUpGrantService stepUp, IValidator<StepUpRequest> validator,
        ICurrentUser currentUser, TenantContext tenant, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await stepUp.IssueAsync(tenant.AccountId, currentUser.UserId, request.Password, ct);
        if (!result.IsSuccess)
            // #336 review — 400, exactly as ChangePassword above returns the SAME
            // Users.CurrentPasswordIncorrect error. A rejected step-up password is a
            // credential rejection, NOT an expired session, and it must not be
            // reported as one: the SPA's apiFetch treats every 401 as a stale access
            // token, silently refreshes, and REPLAYS the identical request. One typed
            // password would then cost TWO failed accesses, cutting the #128 five-
            // attempt account lockout this endpoint just gained to three — while
            // burning a refresh-token rotation per attempt, and signing the user out
            // mid-flow whenever the refresh itself is unavailable. Only the genuinely
            // unauthenticated branch above stays 401.
            return Results.Problem(
                result.Error.Description,
                statusCode: StatusCodes.Status400BadRequest,
                title: result.Error.Code);

        return Results.Ok(new StepUpResponse(result.Value.Token, result.Value.ExpiresAt));
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
    // Same user via both paths is idempotent — the registry keeps the latest
    // instant per user, so it is recorded once, not twice.
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
        HttpRequest request, HttpResponse response, IIdentityProvider identity,
        ICurrentUser currentUser, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!AuthCookies.HasCsrfHeader(request))
            return Results.Problem("Missing required header.", statusCode: 403, title: "Auth.CsrfHeaderRequired");

        if (currentUser.IsResolved)
            await identity.RecordLogoutAsync(currentUser.UserId, ct);

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
            var selectedOutcome = await identity.RevokeRefreshTokenAsync(
                selectedRefreshToken, ct, accountId);
            if (legacyMatchesSelected)
                clearLegacyCookie = selectedOutcome == RefreshTokenRevocationOutcome.InScope;
        }
        if (legacyRefreshToken is not null && !legacyMatchesSelected)
        {
            if (legacyIsOnlyPresentedSession)
                await identity.RevokeRefreshTokenAsync(legacyRefreshToken, ct);
            else if (accountId is not null)
            {
                var legacyOutcome = await identity.RevokeRefreshTokenAsync(legacyRefreshToken, ct, accountId);
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

public sealed record LoginRequest(string FarmCode, string Email, string Password);

public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

// #145 — the refresh token is delivered as a cookie, so the body returns only
// the access token and its expiry.
public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiry);

public sealed record StepUpRequest(string Password);

public sealed record StepUpResponse(string Token, DateTimeOffset ExpiresAt);
