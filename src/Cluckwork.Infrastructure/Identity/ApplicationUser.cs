namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Domain.Catalog;
using Microsoft.AspNetCore.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid AccountId { get; set; }
    public string? DisplayName { get; set; }

    // #45 — the user's UI-language preference, a nullable BCP-47 primary subtag
    // (lowercased). NOT a locale: regional/number/date formatting stays a
    // farm-scoped `Account` concern (§4.5). null = follow the app default.
    public string? Language { get; set; }

    // #444 — overrides Account.DefaultStepperUnit for this user's Daily Entry
    // steppers. null = follow the farm default; set only via SetStepperUnit,
    // which also confirms the unit is still an active EggUnitConversion.
    public EggUnit? PreferredStepperUnit { get; set; }

    // Set true for generated one-time credentials created by offline
    // provisioning commands, never by ordinary user creation. While true, the JWT
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

    // #338 — per-user step-up logout epoch. Each step-up grant embeds the value
    // this held when it was issued; logout increments it; a grant is admitted
    // only while its embedded epoch still equals this one. An INTEGER, compared
    // for equality — never a timestamp — so logout revocation is immune to the
    // wall-clock skew between the replica that issues a grant and the one that
    // records the logout (the #338 review defect). Same shape as CredentialEpoch
    // (#364). Durable in Postgres, not Redis (a never-re-touched key is exactly
    // what maxmemory-LRU evicts first). 0 for every existing row — no backfill.
    public int StepUpLogoutEpoch { get; set; }
}
