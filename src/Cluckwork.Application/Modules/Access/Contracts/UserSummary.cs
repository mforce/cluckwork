namespace Cluckwork.Application.Modules.Access.Contracts;

// #356 — DisabledAt is null for an active user. Exposed on the LIST rather
// than filtered out of it: an Owner cannot re-enable someone they cannot see.
// #1280 — TwoFactorEnabled, so the Users page offers the reset only for an
// enrolled user. #1284 — PasskeyCount, so it offers Remove passkeys only to a
// user who has one.
public sealed record UserSummary(
    Guid Id, string Email, string? DisplayName, string Role, DateTimeOffset? DisabledAt, bool TwoFactorEnabled,
    int PasskeyCount);
