using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #1277 M4: a logout finishes its writes even when the client stops waiting,
// so a late answer never has to be awaited to end the session.
public sealed partial class SecondFactorLoginTests
{
    private Task<(int Epoch, int LiveRows)> LogoutStateAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, async db => (
            await db.Users.Where(u => u.Id == userId).Select(u => u.StepUpLogoutEpoch).SingleAsync(),
            await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null)));

    // A logout whose client gives up once the handler has started its first
    // write; returns after the server has finished both writes.
    private async Task AbortedLogoutAsync(Guid accountId, Guid userId, TokenPairDto session)
    {
        var before = await LogoutStateAsync(accountId, userId);
        using var cancel = new CancellationTokenSource();
        factory.BeforeCommand.Arm("\"StepUpLogoutEpoch\" + 1", async () =>
        {
            await cancel.CancelAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add("Cookie", $"{AuthCookies.RefreshCookieNameFor(accountId)}={session.RefreshToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, accountId.ToString());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.CreateClient(TestHarness.Cookieless(factory)).SendAsync(request, cancel.Token));

        Assert.True(factory.BeforeCommand.Fired, "the logout never reached its first write");
        var after = before;
        for (var wait = 0; wait < 50 && (after.Epoch == before.Epoch || after.LiveRows == before.LiveRows); wait++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            after = await LogoutStateAsync(accountId, userId);
        }
        Assert.True(after.Epoch > before.Epoch, "the logout was not recorded");
        Assert.Equal(before.LiveRows - 1, after.LiveRows);
    }

    [Fact]
    public async Task ALogout_WhoseClientGivesUp_StillRevokesAndRecords()
    {
        var email = $"tf-logout-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdAsync(accountId, email);
        var session = await factory.LoginAsync(email);

        await AbortedLogoutAsync(accountId, userId, session);

        Assert.Equal(0, (await LogoutStateAsync(accountId, userId)).LiveRows);
    }

    // #1277 MC7: the aborted logout's browser keeps its cookie. Presenting it
    // again is a session the user ended, not a theft: it fails quietly and
    // leaves the user's other sessions alone, expired or not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACookieThatLogoutRetired_FailsQuietly_AndLeavesTheOtherSessionsAlone(bool expired)
    {
        var email = $"tf-logout-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdAsync(accountId, email);
        var retired = await factory.LoginAsync(email);
        var other = await factory.LoginAsync(email);
        await AbortedLogoutAsync(accountId, userId, retired);
        if (expired)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(retired.RefreshTokenForDirectCall))).ToLowerInvariant();
            var past = factory.Clock.GetUtcNow().AddMinutes(-1);
            Assert.Equal(1, await factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens
                .Where(t => t.TokenHash == hash).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, past))));
        }
        var replays = factory.EventsFor(SecurityEvents.RefreshTokenReplayDetected).Count;

        var again = await RefreshAsync(accountId, retired.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(string.Empty, TestHarness.ExtractRefreshCookie(again, accountId));
        Assert.Equal(replays, factory.EventsFor(SecurityEvents.RefreshTokenReplayDetected).Count);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(accountId, other.RefreshToken)).StatusCode);
    }
}
