namespace Cluckwork.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string PublicKeyPem { get; init; } = string.Empty;
    public string PrivateKeyPem { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;

    // #176 — idempotency grace for refresh-token reuse-detection. A token rotated
    // within this many seconds whose replacement is still the live tip is treated
    // as a benign concurrent/dead-tab retry (the #169 residual), not a replay, so
    // the racing tab is handed a fresh token instead of the whole family being
    // revoked. Kept short vs the ~15-min refresh cadence to bound the inherent
    // in-window relaxation of theft-detection; set 0 to disable (strict replay).
    public int RefreshReuseGraceSeconds { get; init; } = 10;

    // #308 — step-up grant lifetime (POST /auth/step-up). Short enough to
    // bound what a captured grant is worth, long enough to complete one
    // confirm-password step and fire the one sensitive request it unlocks.
    public int StepUpGrantMinutes { get; init; } = 5;
}
