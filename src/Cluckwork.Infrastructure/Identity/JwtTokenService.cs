namespace Cluckwork.Infrastructure.Identity;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public interface IJwtTokenService
{
    TokenPair CreateTokenPair(ApplicationUser user, IReadOnlyCollection<string> roles, string refreshToken);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : IJwtTokenService
{
    public TokenPair CreateTokenPair(ApplicationUser user, IReadOnlyCollection<string> roles, string refreshToken)
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

        // "role" is the short claim name the API validates against
        // (TokenValidationParameters.RoleClaimType) and the SPA decodes.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new("account_id", user.AccountId.ToString()),
            new("credential_epoch", user.CredentialEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        // #283 — carried only when true (mirrors the role claims' omit-if-absent
        // shape). MustChangePasswordMiddleware reads this to block every
        // endpoint but auth/change-password + auth/logout until the first-run
        // admin (or anyone else whose password was force-reset) sets their own
        // password; claims.ts decodes the same claim to show the SPA's
        // first-login screen instead of the normal app shell. Since #364, the
        // password change that clears this flag also bumps CredentialEpoch, so
        // a token minted BEFORE it — carrying this claim or not — is rejected
        // by CredentialEpochMiddleware on its very next request; the ~15-min
        // access-token lifetime is now only a defense-in-depth ceiling, not
        // what actually bounds the old token's window.
        if (user.MustChangePassword)
            claims.Add(new Claim("must_change_password", "true"));

        var token = new JwtSecurityToken(
            jwtOptions.Issuer,
            jwtOptions.Audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            credentials);

        return new TokenPair(
            new JwtSecurityTokenHandler().WriteToken(token), refreshToken, expires, user.AccountId);
    }
}
