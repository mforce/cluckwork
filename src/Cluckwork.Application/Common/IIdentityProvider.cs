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
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiry);
