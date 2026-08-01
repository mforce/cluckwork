namespace Cluckwork.Application.Common;

// Port — abstraction over ASP.NET Core Identity + JWT. Swap to Keycloak/Entra
// in a future IIdentityProvider implementation without touching Application.
public interface IIdentityProvider
{
    Task<Result<TokenPair>> LoginAsync(
        string email, string password, CancellationToken ct = default);

    Task<Result<TokenPair>> RefreshAsync(
        string refreshToken, CancellationToken ct = default);

    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default);

    // #308/#336 review — record that THIS user logged out, independently of any
    // refresh token. RevokeRefreshTokenAsync already records a logout for the
    // cookie's owner, but the cookie and the caller's access token can name
    // DIFFERENT users: the refresh cookie is per-origin (one per browser, last
    // login wins) while the SPA keeps each access token in its own tab's memory.
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
    // display name (null = none). #283 — mustChangePassword is true ONLY for
    // the `bootstrap-admin` first-run command's Owner; every other caller
    // (the Users page's CreateUser, the demo/simulation seeders) leaves it
    // false — an ordinary new user is not put through the forced-change gate.
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
    // password. Bounded by the access-token lifetime: an already-issued JWT stays
    // valid until it expires (~15 min) — there is no server-side denylist.
    Task<Result> SetUserPasswordAsync(
        Guid accountId, Guid userId, string newPassword, CancellationToken ct = default);

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
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiry);

public sealed record UserSummary(Guid Id, string Email, string? DisplayName, string Role);

public sealed record UserProfile(
    Guid Id, string Email, string? DisplayName, string Role, string? Language);
