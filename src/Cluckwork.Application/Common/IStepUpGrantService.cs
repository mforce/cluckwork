namespace Cluckwork.Application.Common;

using Cluckwork.Domain.Common;

// #308 — recent-authentication proof for security-sensitive user
// administration (creating another Owner; resetting an Owner's password). A
// grant is minted by re-confirming the CALLER's current password
// (IssueAsync) and is a short-lived, single-use, audience-limited credential
// distinct from the normal access token. See the threat-model comment on
// Cluckwork.Infrastructure.Identity.StepUpGrantService for the full design.
public interface IStepUpGrantService
{
    // Verifies currentPassword for (accountId, userId) and mints a grant bound
    // to that account+user. Fails non-enumerating: the same error whether the
    // user cannot be found, belongs to a different account, or the password
    // is simply wrong.
    Task<Result<StepUpGrant>> IssueAsync(
        Guid accountId, Guid userId, string currentPassword, CancellationToken ct = default);

    // Verifies a grant presented for (accountId, userId): well-formed, signed,
    // unexpired, matching account+user, matching the user's CURRENT security
    // stamp (so a password change/reset revokes it), not replayed, and not
    // issued before the user's last logout. Every failure reason maps to the
    // SAME error (StepUpErrorCodes.Required) — the caller must not be able to
    // distinguish "expired" from "revoked" from "missing" (non-enumerating,
    // #308 acceptance criteria).
    Task<Result> ValidateAsync(
        Guid accountId, Guid userId, string? stepUpToken, CancellationToken ct = default);
}

public sealed record StepUpGrant(string Token, DateTimeOffset ExpiresAt);

public static class StepUpErrorCodes
{
    // #308 — every step-up rejection reason (missing, malformed, expired,
    // replayed, wrong-account/user, stamp-revoked, logout-revoked) maps to
    // this one code so a gated endpoint's caller cannot enumerate WHY a grant
    // failed.
    public const string Required = "Identity.StepUpRequired";
}
