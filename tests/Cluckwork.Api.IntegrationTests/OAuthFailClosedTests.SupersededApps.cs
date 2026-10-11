using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.OAuth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #1229 — a credential-epoch bump disconnects the user's apps rather than leaving them
// listed and refused, and a refused request is never recorded as use.
public sealed partial class OAuthFailClosedTests
{
    [Fact]
    public async Task PasswordChange_DisconnectsTheApp()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);

        var jwt = await ChangePasswordAsync(host, user.Jwt);
        var (authorizations, tokens) = await StillValidAsync(user);
        var listed = await Client(host, jwt).GetFromJsonAsync<JsonElement>("/api/v1/me/connected-apps");
        using var retry = await Client(host, app.Token).GetAsync(Probe.Read);

        Assert.True(authorizations == 0, $"{authorizations} authorization(s) stayed valid after a password change");
        Assert.True(tokens == 0, $"{tokens} token(s) stayed valid after a password change");
        Assert.Equal(0, listed.GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
    }

    // A connection the bump did not reach: one approved while the bump committed, or one
    // left from before #1229. Bumping the column directly leaves exactly that behind.
    [Fact]
    public async Task SupersededToken_IsRevoked_OnItsNextRequest()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);
        await BumpEpochBehindTheSweepAsync(user);

        using var retry = await Client(host, app.Token).GetAsync(Probe.Read);
        var (authorizations, tokens) = await StillValidAsync(user);

        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded", await TitleOf(retry));
        Assert.True(authorizations == 0, $"{authorizations} superseded authorization(s) stayed valid");
        Assert.True(tokens == 0, $"{tokens} superseded token(s) stayed valid");
    }

    [Fact]
    public async Task RefusedRequest_DoesNotMoveLastUsed()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);
        (await Client(host, app.Token).GetAsync(Probe.Read)).Dispose();
        // Past the interval, so any stamp would move it.
        await factory.WithTenantScopeAsync(user.AccountId, db => db.OAuthAuthorizations
            .Where(a => a.Subject == user.Id.ToString())
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                a => EF.Property<DateTimeOffset?>(a, OAuthAuthorizationConfiguration.LastUsedAtUtc),
                a => DateTimeOffset.UtcNow - ConnectedApps.LastUsedInterval - TimeSpan.FromMinutes(1))));
        await BumpEpochBehindTheSweepAsync(user);
        var before = await StoredLastUsedAsync(user);

        using var retry = await Client(host, app.Token).GetAsync(Probe.Read);
        var after = await StoredLastUsedAsync(user);

        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.True(after == before, $"a refused request moved last used from {before:O} to {after:O}");
    }

    // Requests with the token race the bump. Whatever order they land in, the bump's
    // revocation stands, and the token is refused afterwards.
    [Fact]
    public async Task PasswordChange_RacingTokenUse_LeavesNothingValid()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);
        var client = Client(host, app.Token);

        var uses = Enumerable.Range(0, 20).Select(async _ => (await client.GetAsync(Probe.Read)).Dispose()).ToList();
        await ChangePasswordAsync(host, user.Jwt);
        await Task.WhenAll(uses);
        var (authorizations, tokens) = await StillValidAsync(user);
        using var retry = await client.GetAsync(Probe.Read);

        Assert.True(authorizations == 0, $"{authorizations} authorization(s) survived the race");
        Assert.True(tokens == 0, $"{tokens} token(s) survived the race");
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_ThenReconnect_Works()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var first = await ConnectAsync(host, user, ReadScope);

        // ApproveAsync steps up with TestHarness.Password, so the second change restores it.
        var changed = await ChangePasswordAsync(host, user.Jwt);
        var jwt = await ChangePasswordAsync(host, changed, TestHarness.Password + "x", TestHarness.Password);
        var again = await ConnectAsync(host, user with { Jwt = jwt }, [ReadScope], first.ClientId);
        using var response = await Client(host, again.Token).GetAsync(Probe.Read);
        var listed = await Client(host, jwt).GetFromJsonAsync<JsonElement>("/api/v1/me/connected-apps");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([first.ClientId], listed.EnumerateArray().Select(row => row.GetProperty("clientId").GetString()));
    }

    private static async Task<string> ChangePasswordAsync(WebApplicationFactory<Program> host, string jwt,
        string currentPassword = TestHarness.Password, string newPassword = TestHarness.Password + "x")
    {
        using var response = await Client(host, jwt).PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword, newPassword });
        response.EnsureSuccessStatusCode();
        return (await TestHarness.ReadTokensAsync(response)).AccessToken;
    }

    // From the row: Connected apps no longer lists a revoked connection.
    private Task<DateTimeOffset?> StoredLastUsedAsync(SeededUser user) =>
        factory.WithTenantScopeAsync(user.AccountId, db => db.OAuthAuthorizations
            .Where(a => a.Subject == user.Id.ToString())
            .Select(a => EF.Property<DateTimeOffset?>(a, OAuthAuthorizationConfiguration.LastUsedAtUtc))
            .SingleAsync());

    private Task BumpEpochBehindTheSweepAsync(SeededUser user) =>
        factory.WithTenantScopeAsync(user.AccountId, db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.CredentialEpoch, u => u.CredentialEpoch + 1)));
}
