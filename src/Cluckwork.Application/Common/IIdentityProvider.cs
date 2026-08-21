namespace Cluckwork.Application.Common;

using Cluckwork.Domain.Catalog;

// Port — abstraction over ASP.NET Core Identity + JWT. Swap to Keycloak/Entra
// in a future IIdentityProvider implementation without touching Application.
public interface IIdentityProvider
{
    // #532 — the account is resolved from the farm code BEFORE this call, and
    // is an INPUT, not something login discovers. Previously the account fell
    // out of whichever row a global email lookup happened to return, so nothing
    // ever CHOSE a farm.
    Task<Result<TokenPair>> LoginAsync(
        Guid accountId, string email, string password, CancellationToken ct = default);

    // #547 — expectedAccountId is the farm the tab told the endpoint it is
    // refreshing, sent as the X-Cluckwork-Account header. The server compares
    // it against the STORED token's AccountId and refuses (Auth.SessionChanged)
    // BEFORE anything rotates when they differ. Per-farm cookie names make a
    // normal mismatch impossible; the comparison remains defence-in-depth
    // against a malformed or misplaced cookie. Absent (null) is reserved for
    // headerless legacy migration and is not a mismatch.
    Task<Result<TokenPair>> RefreshAsync(
        string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null);

    // When expectedAccountId is present, resolve the stored token's account and
    // do nothing unless it matches. Logout uses this for the temporary shared
    // legacy cookie: a browser may hold that cookie for one farm alongside a
    // selected per-farm cookie for another. Null preserves the unconditional
    // legacy-only logout contract.
    Task RevokeRefreshTokenAsync(
        string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null);

    // #308/#336 review — record that THIS user logged out, independently of any
    // refresh token. RevokeRefreshTokenAsync already records a logout for the
    // cookie's owner, but the selected cookie and caller's access token can name
    // different users: the account header routes the per-farm cookie while the
    // SPA keeps each access token in its own tab's memory.
    // So the user who actually clicked logout is the bearer's subject, and only
    // this call reaches them — see AuthEndpoints.Logout.
    //
    // Deliberately narrower than RevokeRefreshTokenAsync: it invalidates the
    // user's outstanding step-up grants WITHOUT revoking their refresh tokens.
    // Ending one tab's session must not sign the user out of every other device,
    // which revoking the whole token family would do.
    Task RecordLogoutAsync(Guid userId, CancellationToken ct = default);

    // #103 — role is one of Roles.Assignable, or null for a plain worker
    // (workers deliberately carry no role row). #163 — name is the optional
    // display name (null = none). mustChangePassword is true for generated
    // one-time credentials from offline provisioning commands; ordinary user
    // creation and the demo/simulation seeders leave it false.
    Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role,
        string? name = null, bool mustChangePassword = false, CancellationToken ct = default);

    // #163 — edit an existing user's display name, scoped to the account so a
    // foreign user id resolves to a NotFound failure, never a cross-tenant edit.
    Task<Result> UpdateUserAsync(
        Guid accountId, Guid userId, string? name, CancellationToken ct = default);

    // #165 — an Owner sets another user's password without knowing the current
    // one. Account-scoped (foreign id -> NotFound) and it REVOKES every refresh
    // token for the target, so a reset actually evicts whoever held the old
    // password. Since #364 it also bumps the target's credential epoch in the
    // same transaction, so an already-issued JWT is rejected on its very next
    // request — no longer merely bounded by the ~15-min access-token lifetime.
    Task<Result> SetUserPasswordAsync(
        Guid accountId, Guid userId, string newPassword, CancellationToken ct = default);

    // #355 — promote/demote an existing user's role, account-scoped (foreign
    // id -> NotFound). `role` is one of Roles.Assignable, or null for a plain
    // worker — same convention as CreateUserAsync. `actingUserId` is
    // re-verified INSIDE the locked transaction to still be an active
    // (non-disabled) Owner — an authorization failure (AppError.Forbidden())
    // if not, since the caller's authentication happened once, before this
    // transaction (and its account-wide lock) ever ran, and their own role
    // could have changed while queued behind it. A true no-op (the requested
    // role already equals the target's full current role-row set) skips ALL
    // side effects: no epoch bump, no revoke, no audit row, no mutation. Any
    // REAL change unconditionally bumps CredentialEpoch and revokes every
    // refresh token for the target (RevokeAllActiveForUserAsync) — both
    // promotion and demotion, per #355's own reasoning that consistency here
    // is cheaper than a rule nobody remembers. Demoting the account's LAST
    // active Owner away from Owner fails with "Users.LastOwner" instead of
    // applying; a concurrent Identity write conflict on the same user fails
    // with "Users.Conflict".
    Task<Result> ChangeUserRoleAsync(
        Guid accountId, Guid userId, string? role, Guid actingUserId, CancellationToken ct = default);

    // #356 — disable a user, account-scoped (foreign id -> NotFound). The
    // DisabledAt flag is only half of it: CredentialEpochMiddleware (#364)
    // already refuses a disabled user, but a flag ALONE means re-enabling
    // resurrects every unexpired access token issued before the disable. So a
    // real disable also bumps CredentialEpoch, rotates the SecurityStamp (the
    // separate credential a step-up grant is validated against, #308) and
    // revokes every refresh token — the same three side effects
    // ChangeUserRoleAsync applies, for the same reasons.
    //
    // `actingUserId` is re-verified INSIDE the account-locked transaction to
    // still be an active, non-disabled Owner (AppError.Forbidden() if not).
    // Disabling the account's LAST ACTIVE Owner fails with "Users.LastOwner":
    // that guard is NOT safe on ConcurrencyStamp alone, because two Owners
    // disabling each other touch different rows and share no concurrency
    // token — hence the account-wide FOR UPDATE lock, taken unconditionally.
    // Disabling an already-disabled user is a TRUE no-op: no second epoch
    // bump, no restamped DisabledAt, no audit row. A concurrent Identity write
    // conflict fails with "Users.Conflict".
    Task<Result> DisableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, string? reason,
        CancellationToken ct = default);

    // #356 — re-enable a disabled user, account-scoped (foreign id ->
    // NotFound). Deliberately ASYMMETRIC with DisableUserAsync on the
    // CREDENTIAL side only: it clears DisabledAt and DisabledBy and writes
    // an audit row, and it must NOT bump CredentialEpoch and must NOT
    // restore the pre-disable value — leaving the epoch where the disable
    // left it is exactly what keeps every pre-disable access token dead.
    // It DOES also rotate SecurityStamp/ConcurrencyStamp (round-3 review of
    // #492): a stale full-entity write from a concurrent SetUserPassword,
    // read before this method ran, would otherwise land after it and
    // silently restore DisabledAt behind a 204. That rotation is required,
    // not optional — do not read "does NOTHING else" as license to drop it.
    // It takes the same account lock so a disable and an enable of the same
    // user cannot interleave into an inconsistent DisabledAt/epoch pair.
    // Enabling an already-active user is a true no-op.
    Task<Result> EnableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default);

    // #265 — offline break-glass recovery for a locked-out account (e.g. a sole
    // Owner with a lost password and no email/SMTP reset path). Same account-
    // scoped reset as SetUserPasswordAsync — sets the password WITHOUT the
    // current one, rotates the SecurityStamp, and revokes every refresh token —
    // but records a DISTINCT audit action ("User.BreakGlassReset") carrying the
    // operator's reason, so a break-glass reset is unmistakable in the log.
    // Invoked only by the offline `recover-admin` CLI command; the caller must
    // have resolved the tenant to accountId first so the audit row can be
    // stamped (IAuditWriter fails closed on an unresolved tenant).
    Task<Result> BreakGlassResetAsync(
        Guid accountId, Guid userId, string newPassword, string? reason,
        CancellationToken ct = default);

    // #165 — self-service change, proving the current password. Revokes every
    // refresh token for the user and returns a FRESH pair, so other devices are
    // signed out while the caller stays signed in on this one.
    Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default);

    Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default);

    // #45 — one user's profile, account-scoped so a foreign id resolves to null,
    // never a cross-tenant read. Role is highest-wins (matches ListUsersAsync).
    Task<UserProfile?> GetUserAsync(Guid accountId, Guid userId, CancellationToken ct = default);

    // #45 — set/clear the user's language, account-scoped (foreign id -> NotFound).
    Task<Result> SetLanguageAsync(
        Guid accountId, Guid userId, string? language, CancellationToken ct = default);

    // #444 — set/clear the user's Daily Entry stepper pack-unit override,
    // account-scoped (foreign id -> NotFound). The caller (SetStepperUnitHandler)
    // has already confirmed a non-null unit is still an active EggUnitConversion —
    // this is a plain write, same shape as SetLanguageAsync.
    Task<Result> SetStepperUnitAsync(
        Guid accountId, Guid userId, EggUnit? unit, CancellationToken ct = default);
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiry,
    // #532 — the farm the token pair belongs to. Login/ChangeOwnPassword know it
    // at mint time; Refresh recovers it from the stored token row. The
    // per-farm refresh cookie (#532 per-farm rename) needs it to know WHICH
    // cookie name to write, because the token value itself is opaque.
    Guid AccountId);

// #356 — DisabledAt is null for an active user. Exposed on the LIST rather
// than filtered out of it: an Owner cannot re-enable someone they cannot see.
public sealed record UserSummary(
    Guid Id, string Email, string? DisplayName, string Role, DateTimeOffset? DisabledAt);

public sealed record UserProfile(
    Guid Id, string Email, string? DisplayName, string Role, string? Language,
    EggUnit? PreferredStepperUnit);
