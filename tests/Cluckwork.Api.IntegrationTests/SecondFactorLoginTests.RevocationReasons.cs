using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1277: every path that revokes a refresh token names why, and only
// Logout fails quietly. A row with no reason, revoked before the column
// existed, stays strict replay.
public sealed partial class SecondFactorLoginTests
{
    private async Task<TokenPairDto> RotateAsync(Guid accountId, TokenPairDto tokens)
    {
        using var response = await RefreshAsync(accountId, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestHarness.ReadTokensAsync(response);
    }

    private static string HashOf(TokenPairDto tokens) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokens.RefreshTokenForDirectCall))).ToLowerInvariant();

    [Theory]
    [InlineData(RefreshRevocationReason.Rotated)]
    [InlineData(RefreshRevocationReason.Logout)]
    [InlineData(RefreshRevocationReason.CredentialsChanged)]
    [InlineData(RefreshRevocationReason.ReplayDetected)]
    [InlineData(RefreshRevocationReason.FarmSuspended)]
    public async Task EveryRevocation_NamesItsReason(RefreshRevocationReason expected)
    {
        var email = $"tf-reason-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var first = await factory.LoginAsync(email);
        var inspected = first;
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        switch (expected)
        {
            case RefreshRevocationReason.Rotated:
                await RotateAsync(accountId, first);
                break;
            case RefreshRevocationReason.Logout:
                using (var answer = await client.PostLogoutAsync(first.RefreshToken, accountId: accountId))
                    Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
                break;
            case RefreshRevocationReason.CredentialsChanged:
                using (var authed = factory.CreateAuthedClient(first.AccessToken))
                using (var answer = await authed.PostAsJsonAsync("/api/v1/auth/change-password",
                    new { currentPassword = TestHarness.Password, newPassword = Guid.NewGuid().ToString("N") + "aA!9" }))
                    Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
                break;
            case RefreshRevocationReason.ReplayDetected:
                inspected = await factory.LoginAsync(email);
                await RotateAsync(accountId, await RotateAsync(accountId, first));
                using (var answer = await RefreshAsync(accountId, first.RefreshToken))
                    Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
                break;
            case RefreshRevocationReason.FarmSuspended:
                using (var scope = factory.Services.CreateScope())
                    Assert.True((await scope.ServiceProvider.GetRequiredService<AccountSuspensionService>()
                        .SuspendAsync(accountId, "reason test")).IsSuccess);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected));
        }

        var hash = HashOf(inspected);
        var row = await factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == hash));
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(expected, row.RevocationReason);
    }

    // A rotated row with no reason is replay: one event, and the family dies.
    [Fact]
    public async Task ARevokedTokenWithNoReason_IsReplay()
    {
        var email = $"tf-reason-null-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var initial = await factory.LoginAsync(email);
        var sibling = await factory.LoginAsync(email);
        await RotateAsync(accountId, await RotateAsync(accountId, initial));
        var hash = HashOf(initial);
        Assert.Equal(1, await factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens.Where(t => t.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevocationReason, (RefreshRevocationReason?)null))));
        var replays = factory.EventsFor(SecurityEvents.RefreshTokenReplayDetected).Count;

        using var refused = await RefreshAsync(accountId, initial.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(replays + 1, factory.EventsFor(SecurityEvents.RefreshTokenReplayDetected).Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(accountId, sibling.RefreshToken)).StatusCode);
    }
}
