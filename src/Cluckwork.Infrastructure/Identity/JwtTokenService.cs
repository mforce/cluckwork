namespace Cluckwork.Infrastructure.Identity;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public interface IJwtTokenService
{
    TokenPair CreateTokenPair(ApplicationUser user, string refreshToken);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : IJwtTokenService
{
    public TokenPair CreateTokenPair(ApplicationUser user, string refreshToken)
    {
        var jwtOptions = options.Value;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PemKey.Normalize(jwtOptions.PrivateKeyPem));

        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(jwtOptions.AccessTokenMinutes);
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim("account_id", user.AccountId.ToString())
        };

        var token = new JwtSecurityToken(
            jwtOptions.Issuer,
            jwtOptions.Audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            credentials);

        return new TokenPair(new JwtSecurityTokenHandler().WriteToken(token), refreshToken, expires);
    }
}
