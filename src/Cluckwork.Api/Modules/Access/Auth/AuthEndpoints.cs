using Cluckwork.Api.Hosting;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.RateLimiting;

namespace Cluckwork.Api.Modules.Access.Auth;

public static partial class AuthEndpoints
{
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

    // In every environment but Development the browser reaches the app over HTTPS
    // (TLS terminates at the proxy), so the auth cookie must be Secure regardless
    // of the internal proxy→app hop scheme.
    private static bool CookieSecure(IWebHostEnvironment env) => !env.IsDevelopment();
}

public sealed record LoginRequest(string FarmCode, string Email, string Password);

public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

// #145 — the refresh token is delivered as a cookie, so the body returns only
// the access token and its expiry.
public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiry);

public sealed record StepUpRequest(string Password);

public sealed record StepUpResponse(string Token, DateTimeOffset ExpiresAt);
