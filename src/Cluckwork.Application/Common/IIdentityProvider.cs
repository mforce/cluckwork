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

    // #73 — minimal user management: admins create workers/admins for their own
    // account. Full role/user administration belongs to the RBAC slice.
    Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, bool isAdmin, CancellationToken ct = default);

    Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default);
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiry);

public sealed record UserSummary(Guid Id, string Email, string? DisplayName, string Role);
