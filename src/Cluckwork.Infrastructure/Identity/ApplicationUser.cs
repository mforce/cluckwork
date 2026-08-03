namespace Cluckwork.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid AccountId { get; set; }
    public string? DisplayName { get; set; }

    // #45 — the user's UI-language preference, a nullable BCP-47 primary subtag
    // (lowercased). NOT a locale: regional/number/date formatting stays a
    // farm-scoped `Account` concern (§4.5). null = follow the app default.
    public string? Language { get; set; }

    // #283 — set true ONLY by the `bootstrap-admin` first-run command on the
    // admin it creates (never by ordinary user creation). While true, the JWT
    // carries a matching claim (JwtTokenService) and MustChangePasswordMiddleware
    // blocks every endpoint except auth/change-password and auth/logout, so a
    // freshly generated first-run secret can't sit unchanged indefinitely. Any
    // successful password reset (self-service change, an Owner's SetUserPassword,
    // or break-glass recovery) clears it — see IdentityProvider.
    public bool MustChangePassword { get; set; }

    // #364 — every access and refresh credential is bound to this monotonic
    // version. Zero is permanently retired so data written by a pre-epoch binary
    // can never match a live user.
    public int CredentialEpoch { get; set; } = 1;

    // #364 ships the readers but no mutation endpoint; #356 will supply the
    // audited administration workflow that sets these fields.
    public DateTimeOffset? DisabledAt { get; set; }
    public Guid? DisabledBy { get; set; }
}
