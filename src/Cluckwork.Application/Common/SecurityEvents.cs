namespace Cluckwork.Application.Common;

// #273 — stable structured security-event identifiers, safe for a deployment
// backend to alert on (brute-force, replay, abnormal rejection rates) without
// parsing free-text log messages. Each name is permanent once shipped — renaming
// one breaks every alert rule built against it — so treat this list like a
// public API: add, never rename or repurpose.
//
// Lives in Application (not Api or Infrastructure) because emission happens on
// both sides of the Api -> Infrastructure boundary: IdentityProvider
// (Infrastructure) owns login/lockout/refresh-replay/revocation, while the
// rate-limiter's OnRejected callback (Api) owns the rate-limit rejection. Both
// already depend on Application; putting the shared vocabulary here avoids
// either layer reaching sideways into the other's namespace for a handful of
// string constants.
//
// Emitted as the {SecurityEvent} structured property on every event below. See
// docs/security/log-redaction-policy.md for the field contract each one
// carries — and what it deliberately never carries: no password, token,
// cookie, or raw connection string, and (Auth.LoginFailed specifically) no
// signal that would make a failed login for a nonexistent user distinguishable
// from a wrong password for a real one.
public static class SecurityEvents
{
    // Fires on EVERY unsuccessful /auth/login or /auth/step-up password check —
    // unknown email, an already-locked account, or a genuinely wrong password —
    // with the IDENTICAL field set on all three branches. Never carries a user
    // id or email: that would let the three branches be told apart from the
    // log side even though the HTTP response already collapses them into one
    // generic 401 (Identity.InvalidCredentials).
    public const string LoginFailed = "Auth.LoginFailed";

    // Fires exactly once per lockout episode: the specific failed attempt that
    // crosses the configured threshold and transitions the account from
    // unlocked to locked. A later attempt against an already-locked account
    // re-fires LoginFailed but NOT this — the transition already happened.
    public const string AccountLockedOut = "Auth.AccountLockedOut";

    // Fires when a presented refresh token is found already revoked/rotated
    // AND the #176 grace-replacement check rules out a benign concurrent
    // retry — i.e. a genuine reuse of a dead token, treated as possible theft.
    public const string RefreshTokenReplayDetected = "Auth.RefreshTokenReplayDetected";

    // Fires when the app's own attempt to revoke a refresh token (or a whole
    // token family, following a replay) THROWS instead of completing — the
    // safety action meant to lock a suspected attacker out failed to run.
    // Distinct from ReplayDetected: replay detection can succeed even when the
    // subsequent revoke attempt fails, and each is worth alerting on
    // separately.
    public const string RefreshRevocationFailed = "Auth.RefreshRevocationFailed";

    // Fires when the per-IP fixed-window limiter rejects a request against the
    // login or refresh policy (RateLimitingOptions.LoginPolicyName /
    // RefreshPolicyName) with 429. Deliberately excludes the client-errors
    // policy (#217) — that budget guards log-pipeline volume, not a
    // credential, so its rejections carry no security-event line.
    public const string RateLimitRejected = "Auth.RateLimitRejected";
}
