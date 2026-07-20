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
    // (workers deliberately carry no role row).
    Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role, CancellationToken ct = default);

    Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default);
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiry);

public sealed record UserSummary(Guid Id, string Email, string? DisplayName, string Role);
