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

    // #103 — role is one of Roles.Assignable, or null for a plain worker
    // (workers deliberately carry no role row). #163 — name is the optional
    // display name (null = none).
    Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role,
        string? name = null, CancellationToken ct = default);

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

    // #165 — self-service change, proving the current password. Revokes every
    // refresh token for the user and returns a FRESH pair, so other devices are
    // signed out while the caller stays signed in on this one.
    Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default);

    Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default);
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiry);

public sealed record UserSummary(Guid Id, string Email, string? DisplayName, string Role);
