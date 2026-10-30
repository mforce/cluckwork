using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.IdentityModel.Tokens;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

internal readonly record struct ChallengeClaims(Guid AccountId, Guid UserId, ProofBinding Binding);

// #1274 decisions 1 to 3: what a correct password earns an enrolled user. A JWT
// under Jwt:Issuer with its own literal audience, so the bearer handler refuses
// it (as StepUpGrantService.Audience is refused). It carries sf_epoch, never
// credential_epoch, so CredentialEpochMiddleware refuses it too if the audience
// check were bypassed. No role, and the stamp only as a hash.
internal static class SecondFactorChallenge
{
    internal const string Audience = "cluckwork-second-factor";
    internal const string EpochClaim = "sf_epoch";
    internal const string SignInsClaim = "sf_signins";
    internal const string StampClaim = "sf_stamp";

    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // A real challenge is a few hundred bytes; this caps parsing, as
    // StepUpGrantService.ValidateAsync caps the grant.
    private const int MaxLength = 4096;

    internal static LoginStep.SecondFactorRequired Issue(ApplicationUser user, JwtOptions options, DateTimeOffset now)
    {
        var expires = now + Lifetime;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PemKey.Normalize(options.PrivateKeyPem));
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("account_id", user.AccountId.ToString()),
            new(EpochClaim, user.CredentialEpoch.ToString(CultureInfo.InvariantCulture)),
            new(SignInsClaim, user.SecondFactorSignIns.ToString(CultureInfo.InvariantCulture)),
            new(StampClaim, ProofBinding.HashStamp(user.SecurityStamp)),
        ];
        var token = new JwtSecurityToken(options.Issuer, Audience, claims, now.UtcDateTime, expires.UtcDateTime, credentials);
        return new LoginStep.SecondFactorRequired(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    // Null for an expired, forged or malformed challenge: all are one answer.
    // The lifetime is checked against the host TimeProvider, as the step-up
    // grant's is, so tests move time instead of sleeping.
    internal static ChallengeClaims? Read(string? challenge, JwtOptions options, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(challenge) || challenge.Length > MaxLength) return null;

        using var rsa = RSA.Create();
        rsa.ImportFromPem(PemKey.Normalize(options.PublicKeyPem));
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(rsa)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            },
            ValidateLifetime = false,
        };

        JwtSecurityToken jwt;
        try
        {
            new JwtSecurityTokenHandler().ValidateToken(challenge, parameters, out var validated);
            jwt = (JwtSecurityToken)validated;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }

        if (new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero) <= now) return null;

        string? Claim(string type) => jwt.Claims.FirstOrDefault(c => c.Type == type)?.Value;
        return Guid.TryParse(Claim(JwtRegisteredClaimNames.Sub), out var userId)
            && Guid.TryParse(Claim("account_id"), out var accountId)
            && int.TryParse(Claim(EpochClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            && long.TryParse(Claim(SignInsClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var signIns)
            && Claim(StampClaim) is { } stampHash
            ? new ChallengeClaims(accountId, userId, new ProofBinding(epoch, stampHash, signIns))
            : null;
    }
}
