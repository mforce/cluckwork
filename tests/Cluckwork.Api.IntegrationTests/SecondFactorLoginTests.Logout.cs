using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
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

    [Fact]
    public async Task ALogout_WhoseClientGivesUp_StillRevokesAndRecords()
    {
        var email = $"tf-logout-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdAsync(accountId, email);
        var session = await factory.LoginAsync(email);
        var before = await LogoutStateAsync(accountId, userId);
        Assert.Equal(1, before.LiveRows);
        using var cancel = new CancellationTokenSource();
        // The client gives up once the handler has started its first write.
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
        for (var wait = 0; wait < 50 && (after.Epoch == before.Epoch || after.LiveRows != 0); wait++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            after = await LogoutStateAsync(accountId, userId);
        }
        Assert.True(after.Epoch > before.Epoch, "the logout was not recorded");
        Assert.Equal(0, after.LiveRows);
    }
}
