using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    // write; returns after the server has finished every write, leaving
    // `remaining` live sessions. Without a bearer only the cookie is sent.
    private async Task AbortedLogoutAsync(Guid accountId, Guid userId, string cookies, string? accessToken, int remaining)
    {
        var before = await LogoutStateAsync(accountId, userId);
        Assert.True(before.LiveRows > remaining, $"the setup left {before.LiveRows} live session(s), so the logout has nothing to revoke");
        using var cancel = new CancellationTokenSource();
        var abortedAtStart = true;
        var abortSeen = false;
        factory.BeforeCommand.Arm("\"StepUpLogoutEpoch\" + 1", async () =>
        {
            var aborted = factory.Services.GetRequiredService<IHttpContextAccessor>().HttpContext!.RequestAborted;
            abortedAtStart = aborted.IsCancellationRequested;
            await cancel.CancelAsync();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), aborted);
            }
            catch (OperationCanceledException)
            {
                abortSeen = true;
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add("Cookie", cookies);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, accountId.ToString());
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.CreateClient(TestHarness.Cookieless(factory)).SendAsync(request, cancel.Token));

        Assert.True(factory.BeforeCommand.Fired, "the logout never reached its first write");
        Assert.False(abortedAtStart, "the client gave up before the handler started");
        Assert.True(abortSeen, "the server never saw the client give up");
        var after = before;
        for (var wait = 0; wait < 50 && (after.Epoch == before.Epoch || after.LiveRows != remaining); wait++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            after = await LogoutStateAsync(accountId, userId);
        }
        Assert.True(after.Epoch > before.Epoch, "the logout was not recorded");
        Assert.Equal(remaining, after.LiveRows);
    }

    // Each revoke the handler can make: the selected farm's cookie, a legacy
    // cookie presented alone, and a legacy cookie beside the selected one.
    [Theory]
    [InlineData("selected")]
    [InlineData("legacy alone")]
    [InlineData("legacy beside selected")]
    public async Task ALogout_WhoseClientGivesUp_StillRevokesAndRecords(string cookies)
    {
        var email = $"tf-logout-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdAsync(accountId, email);
        var session = await factory.LoginAsync(email);
        var selected = $"{AuthCookies.RefreshCookieNameFor(accountId)}={session.RefreshToken}";
        var (cookie, bearer) = cookies switch
        {
            "selected" => (selected, session.AccessToken),
            "legacy alone" => ($"{AuthCookies.LegacyRefreshCookieName}={session.RefreshToken}", null),
            "legacy beside selected" => ($"{selected}; {AuthCookies.LegacyRefreshCookieName}={(await factory.LoginAsync(email)).RefreshToken}", session.AccessToken),
            _ => throw new ArgumentOutOfRangeException(nameof(cookies)),
        };

        await AbortedLogoutAsync(accountId, userId, cookie, bearer, remaining: 0);
    }

    // #1277 N2: a Worker's request reads its flock assignments before the
    // handler. Logout reads no flock data, so that read must not run: a client
    // that gave up during it would cancel the logout before either write.
    [Fact]
    public async Task AWorkersLogout_WhoseClientGivesUpDuringTheFlockRead_StillRevokesAndRecords()
    {
        var ownerEmail = $"tf-logout-owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var email = $"tf-logout-worker-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role: null);
        var userId = await UserIdAsync(accountId, email);
        var session = await factory.LoginAsync(email);
        var before = await LogoutStateAsync(accountId, userId);
        Assert.Equal(1, before.LiveRows);
        using var cancel = new CancellationTokenSource();
        factory.BeforeCommand.Arm("\"UserRoleAssignments\"", async () =>
        {
            await cancel.CancelAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add("Cookie", $"{AuthCookies.RefreshCookieNameFor(accountId)}={session.RefreshToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, accountId.ToString());

        try
        {
            using var response = await factory.CreateClient(TestHarness.Cookieless(factory)).SendAsync(request, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // The hook gave up for the client; the server must carry on.
        }

        Assert.False(factory.BeforeCommand.Fired, "logout read the Worker's flock assignments");
        factory.BeforeCommand.Disarm();
        var after = before;
        for (var wait = 0; wait < 50 && (after.Epoch == before.Epoch || after.LiveRows != 0); wait++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            after = await LogoutStateAsync(accountId, userId);
        }
        Assert.True(after.Epoch > before.Epoch, "the Worker's logout was not recorded");
        Assert.Equal(0, after.LiveRows);
    }

    // #1277 MC7: the aborted logout's browser keeps its cookie. Presenting it
    // again is a session the user ended, not a theft: it fails quietly, clears
    // the cookie and leaves the user's other sessions alone, expired or not,
    // for the selected farm's cookie and the legacy one (R1). MC9: it records
    // Auth.RefreshTokenAfterLogout once, with the fields its policy row names.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ACookieThatLogoutRetired_FailsQuietly_AndLeavesTheOtherSessionsAlone(bool expired, bool legacy)
    {
        var email = $"tf-logout-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdAsync(accountId, email);
        var retired = await factory.LoginAsync(email);
        var other = await factory.LoginAsync(email);
        var name = legacy ? AuthCookies.LegacyRefreshCookieName : AuthCookies.RefreshCookieNameFor(accountId);
        await AbortedLogoutAsync(accountId, userId, $"{name}={retired.RefreshToken}", legacy ? null : retired.AccessToken, remaining: 1);
        if (expired)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(retired.RefreshTokenForDirectCall))).ToLowerInvariant();
            var past = factory.Clock.GetUtcNow().AddMinutes(-1);
            Assert.Equal(1, await factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens
                .Where(t => t.TokenHash == hash).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, past))));
        }
        var replays = factory.EventsFor(SecurityEvents.RefreshTokenReplayDetected).Count;
        var afterLogout = factory.EventsFor(SecurityEvents.RefreshTokenAfterLogout).Count;

        using var again = legacy
            ? await factory.CreateClient(TestHarness.Cookieless(factory)).PostRefreshRawAsync($"{name}={retired.RefreshToken}")
            : await RefreshAsync(accountId, retired.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("Identity.InvalidRefreshToken", (await TitleOf(again)).Title);
        Assert.True(ClearsCookie(again, name), $"the refusal did not clear {name}");
        Assert.Equal(replays, factory.EventsFor(SecurityEvents.RefreshTokenReplayDetected).Count);
        var events = factory.EventsFor(SecurityEvents.RefreshTokenAfterLogout);
        Assert.Equal(afterLogout + 1, events.Count);
        Assert.StartsWith("`UserId`, `ClientIp`.", PolicyFieldsFor(SecurityEvents.RefreshTokenAfterLogout));
        Assert.Equal(["ClientIp", "SecurityEvent", "UserId"], events[^1].Properties.Keys.Where(key => !HostMetadata.Contains(key)).Order());
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(accountId, other.RefreshToken)).StatusCode);
    }

    // R1 clears only a legacy cookie logout retired. Another farm's legacy
    // token fails the farm check, and its cookie stays: it is that farm's
    // live session.
    [Fact]
    public async Task AnotherFarmsLegacyCookie_IsNotCleared()
    {
        var email = $"tf-legacy-a-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var foreignEmail = $"tf-legacy-b-{Guid.NewGuid():N}@test.local";
        var foreignAccountId = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreign = await factory.LoginAsync(foreignEmail);
        var cookie = $"{AuthCookies.LegacyRefreshCookieName}={foreign.RefreshToken}";

        using var refused = await factory.CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshRawAsync(cookie, expectedAccount: accountId.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.False(ClearsCookie(refused, AuthCookies.LegacyRefreshCookieName), "another farm's legacy cookie was cleared");
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(foreignAccountId, foreign.RefreshToken)).StatusCode);
    }

    // A Set-Cookie that deletes the cookie, not merely one with no value.
    private static bool ClearsCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(name + "=;", StringComparison.Ordinal)
            && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
}
