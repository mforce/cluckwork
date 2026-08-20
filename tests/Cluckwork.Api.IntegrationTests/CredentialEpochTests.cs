namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

[Collection(IntegrationCollection.Name)]
public sealed class CredentialEpochTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task ChangeOwnPassword_RejectsTheAccessTokenIssuedBeforeTheCredentialEpochBump()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var beforeReset = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(beforeReset.AccessToken);

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = TestHarness.Password,
            newPassword = CreatePassword(),
        });
        response.EnsureSuccessStatusCode();
        var afterReset = await TestHarness.ReadTokensAsync(response);

        var staleClient = factory.CreateAuthedClient(beforeReset.AccessToken);
        var staleResponse = await staleClient.GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, staleResponse.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded", (await staleResponse.Content
            .ReadFromJsonAsync<ProblemDetails>())!.Title);

        // Logout intentionally remains reachable for a stale bearer so the SPA
        // can clear its cookie and local state after the 401.
        Assert.Equal(HttpStatusCode.NoContent, (await staleClient.PostLogoutAsync(
            beforeReset.RefreshToken, accessToken: beforeReset.AccessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateAuthedClient(afterReset.AccessToken)
            .GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task AccessTokenWithoutCredentialEpoch_IsRejected()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdForAsync(accountId, email);

        var response = await factory.CreateAuthedClient(CreateAccessToken(userId, accountId, null))
            .GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AccessTokenWithMalformedCredentialEpoch_IsRejected()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdForAsync(accountId, email);

        var response = await factory.CreateAuthedClient(CreateAccessToken(userId, accountId, "not-an-epoch"))
            .GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AccessTokenCannotCrossTheUserAccountBoundary()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdForAsync(accountId, email);
        var foreignAccountId = await factory.SeedAccountWithUserAsync(
            $"epoch-{Guid.NewGuid():N}@test.local");

        var response = await factory.CreateAuthedClient(CreateAccessToken(
                userId, foreignAccountId, "1"))
            .GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PreviousEpochRefreshReplay_DoesNotRevokeTheFreshEpochSession()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var beforeReset = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(beforeReset.AccessToken);

        var passwordChange = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = TestHarness.Password,
            newPassword = CreatePassword(),
        });
        passwordChange.EnsureSuccessStatusCode();
        var fresh = await TestHarness.ReadTokensAsync(passwordChange);

        // The old token is already revoked by the password change. It must fail
        // before the reuse detector, rather than revoking this new epoch's token.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(beforeReset.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().PostRefreshAsync(fresh.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
    }

    [Fact]
    public async Task GraceReplacementWrittenByAPreEpochReplica_IsRejectedInertly()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var presented = await factory.LoginAsync(email);
        var presentedRowId = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var userId = await db.Users.Where(user => user.Email == email)
                .Select(user => user.Id).SingleAsync();
            return await db.RefreshTokens.Where(token => token.UserId == userId)
                .Select(token => token.Id).SingleAsync();
        });
        var currentEpochSibling = await factory.LoginAsync(email);
        var replacementRaw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var replacementHash = HashToken(replacementRaw);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            var presentedRow = await db.RefreshTokens.SingleAsync(token => token.Id == presentedRowId);
            var now = DateTimeOffset.UtcNow;

            presentedRow.RevokedAt = now;
            presentedRow.ReplacedByTokenHash = replacementHash;
            presentedRow.ConcurrencyStamp = Guid.NewGuid().ToString();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AccountId = accountId,
                TokenHash = replacementHash,
                IssuedEpoch = 0,
                CreatedAt = now,
                ExpiresAt = now.AddDays(1),
            });
            await db.SaveChangesAsync();
        });

        var response = await factory.CreateClient().PostRefreshAsync(presented.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var revokedAt = await factory.WithTenantScopeAsync(accountId, async db => await db.RefreshTokens
            .Where(token => token.TokenHash == replacementHash)
            .Select(token => token.RevokedAt)
            .SingleAsync());
        Assert.Null(revokedAt);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().PostRefreshAsync(currentEpochSibling.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
    }

    [Fact]
    public async Task RetiredEpochRefresh_IsRejectedInertly()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var currentEpochSibling = await factory.LoginAsync(email);
        var retiredRaw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var retiredHash = HashToken(retiredRaw);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            var now = DateTimeOffset.UtcNow;
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AccountId = accountId,
                TokenHash = retiredHash,
                // Deliberately omit IssuedEpoch: the database default must
                // produce permanently retired epoch zero.
                CreatedAt = now,
                ExpiresAt = now.AddDays(1),
            });
            await db.SaveChangesAsync();
        });

        var response = await factory.CreateClient().PostRefreshAsync(retiredRaw);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var retired = await factory.WithTenantScopeAsync(accountId, async db => await db.RefreshTokens
            .Where(token => token.TokenHash == retiredHash)
            .Select(token => new { token.IssuedEpoch, token.RevokedAt })
            .SingleAsync());
        Assert.Equal(0, retired.IssuedEpoch);
        Assert.Null(retired.RevokedAt);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().PostRefreshAsync(currentEpochSibling.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
    }

    [Fact]
    public async Task DisabledUser_CannotLoginRefreshOrObtainAStepUpGrant()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(user => user.Email == email);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

        var login = await factory.TryLoginAsync(email, TestHarness.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        var stepUp = await factory.CreateAuthedClient(tokens.AccessToken)
            .PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, stepUp.StatusCode);
        Assert.Equal("Auth.AccountDisabled", (await stepUp.Content
            .ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task DisabledLogin_DoesNotRevealPasswordValidityOrMutateLockoutState()
    {
        var disabledEmail = $"epoch-{Guid.NewGuid():N}@test.local";
        var disabledAccountId = await factory.SeedAccountWithUserAsync(disabledEmail);
        await factory.WithTenantScopeAsync(disabledAccountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == disabledEmail);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        var enabledEmail = $"epoch-{Guid.NewGuid():N}@test.local";
        var enabledAccountId = await factory.SeedAccountWithUserAsync(enabledEmail);

        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
        // #532 — each probe must target the account its user is actually IN.
        // SeedAccountWithUserAsync mints a fresh account per call, so passing
        // SeedDefaults.AccountId made all three lookups miss and every assertion
        // below hold for the wrong reason: this test proves a DISABLED user's
        // failure is indistinguishable from a wrong password, and it proved
        // nothing at all while no user was ever found.
        var disabledWrong = await identity.LoginAsync(disabledAccountId, disabledEmail, CreatePassword());
        var disabledCorrect = await identity.LoginAsync(disabledAccountId, disabledEmail, TestHarness.Password);
        var enabledWrong = await identity.LoginAsync(enabledAccountId, enabledEmail, CreatePassword());

        Assert.True(disabledWrong.IsFailure);
        Assert.True(disabledCorrect.IsFailure);
        Assert.True(enabledWrong.IsFailure);
        Assert.Equal(enabledWrong.Error, disabledWrong.Error);
        Assert.Equal(enabledWrong.Error, disabledCorrect.Error);
        var lockoutState = await factory.WithTenantScopeAsync(disabledAccountId, async db => await db.Users
            .Where(user => user.Email == disabledEmail)
            .Select(user => new { user.AccessFailedCount, user.LockoutEnd })
            .SingleAsync());
        Assert.Equal(0, lockoutState.AccessFailedCount);
        Assert.Null(lockoutState.LockoutEnd);
    }

    [Fact]
    public async Task DisabledUser_AccessTokenIsRejectedWithTheDisabledReason()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

        var response = await factory.CreateAuthedClient(tokens.AccessToken).GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.AccountDisabled", (await response.Content
            .ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task SupersededWriteIsRejectedBeforeIdempotencyRequiresAKey()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            user.CredentialEpoch++;
            await db.SaveChangesAsync();
        });

        // Deliberately omit Idempotency-Key. The credential gate must answer
        // first, so a rejected write cannot consume or demand a key.
        var response = await factory.CreateAuthedClient(tokens.AccessToken)
            .PostAsJsonAsync("/api/v1/expense-categories", new { name = "Must not run" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Auth.CredentialsSuperseded", problem.Title);
        Assert.DoesNotContain("Idempotency-Key", problem.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupersededBearer_CanReachTheTrailingSlashLogoutRoute()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            user.CredentialEpoch++;
            await db.SaveChangesAsync();
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout/");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add("Cookie", $"{AuthCookies.RefreshCookieNameFor(accountId)}={tokens.RefreshToken}");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, accountId.ToString());

        var response = await factory.CreateAuthedClient(tokens.AccessToken).SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task SupersededPendingUser_IsRejectedBeforeTheMustChangePasswordGate()
    {
        var accountId = Guid.NewGuid();
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Epoch Gate Farm", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(accountId, email);
        var accessToken = await factory.LoginForAccessTokenAsync(email);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            user.CredentialEpoch++;
            await db.SaveChangesAsync();
        });

        var response = await factory.CreateAuthedClient(accessToken).GetAsync("/api/v1/flocks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded", (await response.Content
            .ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Login_StampsRefreshTokenAndAccessTokenWithTheCurrentEpoch()
    {
        var email = $"epoch-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);

        var user = await factory.WithTenantScopeAsync(accountId, async db => await db.Users
            .Where(candidate => candidate.Email == email)
            .Select(candidate => new { candidate.Id, candidate.CredentialEpoch })
            .SingleAsync());
        var issuedEpoch = await factory.WithTenantScopeAsync(accountId, async db => await db.RefreshTokens
            .Where(token => token.UserId == user.Id)
            .Select(token => token.IssuedEpoch)
            .SingleAsync());
        var accessEpoch = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken)
            .Claims.Single(claim => claim.Type == "credential_epoch").Value;

        Assert.Equal(user.CredentialEpoch, issuedEpoch);
        Assert.Equal(user.CredentialEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), accessEpoch);
    }

    private Task<Guid> UserIdForAsync(Guid accountId, string email) =>
        factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync());

    private static string CreateAccessToken(Guid userId, Guid accountId, string? credentialEpoch)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(TestJwtKeys.PrivateKeyPem.Replace("\\n", "\n", StringComparison.Ordinal));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("account_id", accountId.ToString()),
            new("role", Roles.Owner),
        };
        if (credentialEpoch is not null)
            claims.Add(new Claim("credential_epoch", credentialEpoch));

        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        var token = new JwtSecurityToken(
            "cluckwork-test", "cluckwork-api-test", claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreatePassword() =>
        $"Aa1!{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))}";

    private static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
