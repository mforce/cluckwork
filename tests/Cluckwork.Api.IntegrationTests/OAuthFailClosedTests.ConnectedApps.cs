using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.OAuth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #799 — Connected apps: a person sees and disconnects their own apps, an Owner every app
// on their farm and no other, and a disconnected app is refused on its next request.
public sealed partial class OAuthFailClosedTests
{
    [Fact]
    public async Task ConnectedApps_ListsOnlyYourOwn()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var colleague = await SeedOnFarmAsync(user.AccountId, Roles.Manager);
        var mine = await ConnectAsync(host, user, ReadScope);
        await ConnectAsync(host, colleague, ReadScope);

        var apps = await Client(host, user.Jwt).GetFromJsonAsync<JsonElement>("/api/v1/me/connected-apps");

        Assert.Equal([mine.ClientId], apps.EnumerateArray().Select(app => app.GetProperty("clientId").GetString()));
    }

    [Fact]
    public async Task FarmConnectedApps_AreOwnerOnly()
    {
        using var host = Host();
        var manager = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, manager, ReadScope);

        using var listed = await Client(host, manager.Jwt).GetAsync("/api/v1/users/connected-apps");
        using var disconnected = await DisconnectAsync(host, manager.Jwt, $"/api/v1/users/{manager.Id}/connected-apps?clientId={Uri.EscapeDataString(app.ClientId)}");

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disconnected.StatusCode);
    }

    [Fact]
    public async Task Owner_SeesAndDisconnectsOnlyTheirOwnFarm()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var stranger = await SeedAsync(Roles.Manager);
        var owner = await factory.LoginForAccessTokenAsync(user.OwnerEmail);
        var app = await ConnectAsync(host, user, ReadScope);
        var theirs = await ConnectAsync(host, stranger, ReadScope);

        var farm = await Client(host, owner).GetFromJsonAsync<JsonElement>("/api/v1/users/connected-apps");
        using var refused = await DisconnectAsync(host, owner, $"/api/v1/users/{stranger.Id}/connected-apps?clientId={Uri.EscapeDataString(theirs.ClientId)}");
        using var stillWorks = await Client(host, theirs.Token).GetAsync(Probe.Read);

        Assert.Equal([(user.Id, app.ClientId)], farm.EnumerateArray()
            .Select(row => (row.GetProperty("userId").GetGuid(), row.GetProperty("clientId").GetString()!)));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.True(stillWorks.StatusCode == HttpStatusCode.OK, "an Owner disconnected another farm's app");
    }

    // Each approval that asks for more creates another authorization, and two approvals
    // can race (#798), so one app can hold several. Disconnect revokes them all.
    [Fact]
    public async Task Disconnect_RevokesEveryAuthorizationOfTheApp_AndItsTokens()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var first = await ConnectAsync(host, user, ReadScope);
        await ConnectAsync(host, user, [ReadScope, WriteScope], first.ClientId);

        var before = await Client(host, user.Jwt).GetFromJsonAsync<JsonElement>("/api/v1/me/connected-apps");
        using var disconnected = await DisconnectAsync(host, user.Jwt, $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(first.ClientId)}");
        var (authorizations, tokens) = await StillValidAsync(user);

        Assert.Equal([ReadScope, WriteScope], before.EnumerateArray().Single().GetProperty("scopes")
            .EnumerateArray().Select(scope => scope.GetString()));
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.True(authorizations == 0, $"{authorizations} authorization(s) of the app stayed valid");
        Assert.True(tokens == 0, $"{tokens} token(s) of the app stayed valid");
    }

    [Fact]
    public async Task DisconnectedApp_IsRefused_OnItsNextRequest()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var owner = await factory.LoginForAccessTokenAsync(user.OwnerEmail);
        var own = await ConnectAsync(host, user, ReadScope);
        var other = await ConnectAsync(host, user, ReadScope);
        using (var before = await Client(host, own.Token).GetAsync(Probe.Read))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var self = await DisconnectAsync(host, user.Jwt, $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(own.ClientId)}");
        using var byOwner = await DisconnectAsync(host, owner, $"/api/v1/users/{user.Id}/connected-apps?clientId={Uri.EscapeDataString(other.ClientId)}");
        using var afterSelf = await Client(host, own.Token).GetAsync(Probe.Read);
        using var afterOwner = await Client(host, other.Token).GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.NoContent, self.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byOwner.StatusCode);
        Assert.True(afterSelf.StatusCode == HttpStatusCode.Unauthorized, "a disconnected app still worked");
        Assert.True(afterOwner.StatusCode == HttpStatusCode.Unauthorized, "an app the Owner disconnected still worked");
    }

    [Fact]
    public async Task Disconnect_IsAudited_WithThePersonWhoDidIt()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var owner = await factory.LoginForAccessTokenAsync(user.OwnerEmail);
        var ownerId = await factory.WithTenantScopeAsync(user.AccountId, db =>
            db.Users.Where(u => u.Email == user.OwnerEmail).Select(u => u.Id).SingleAsync());
        var own = await ConnectAsync(host, user, ReadScope);
        var other = await ConnectAsync(host, user, ReadScope);

        (await DisconnectAsync(host, user.Jwt, $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(own.ClientId)}")).Dispose();
        (await DisconnectAsync(host, owner, $"/api/v1/users/{user.Id}/connected-apps?clientId={Uri.EscapeDataString(other.ClientId)}")).Dispose();
        using var nothingLeft = await DisconnectAsync(host, user.Jwt, $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(own.ClientId)}");

        var rows = await factory.WithTenantScopeAsync(user.AccountId, db => db.AuditEvents
            .Where(e => e.Action == AuditActions.UserAppDisconnected)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => new { e.ActorUserId, e.EntityType, e.EntityId, e.ConnectedAppClientId, e.DetailsJson })
            .ToListAsync());
        Assert.Equal(HttpStatusCode.NotFound, nothingLeft.StatusCode);
        Assert.True(rows.Count == 2, $"expected one audit row per disconnect, found {rows.Count}");
        Assert.Equal([user.Id, ownerId], rows.Select(row => row.ActorUserId));
        Assert.All(rows, row => Assert.Equal(("User", user.Id, (string?)null), (row.EntityType, row.EntityId, row.ConnectedAppClientId)));
        Assert.Equal([own.ClientId, other.ClientId], rows.Select(row =>
            JsonDocument.Parse(row.DetailsJson!).RootElement.GetProperty("clientId").GetString()));
    }

    // One conditional write per Interval per authorization, not one per request.
    [Fact]
    public async Task LastUsed_IsStampedAtMostOncePerInterval()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);

        (await Client(host, app.Token).GetAsync(Probe.Read)).Dispose();
        var first = await LastUsedAsync(host, user);
        (await Client(host, app.Token).GetAsync(Probe.Read)).Dispose();
        var second = await LastUsedAsync(host, user);
        await factory.WithTenantScopeAsync(user.AccountId, db => db.OAuthAuthorizations
            .Where(a => a.Subject == user.Id.ToString())
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                a => EF.Property<DateTimeOffset?>(a, OAuthAuthorizationConfiguration.LastUsedAtUtc),
                a => DateTimeOffset.UtcNow - OAuthLastUsedStamp.Interval - TimeSpan.FromMinutes(1))));
        var backdated = await LastUsedAsync(host, user);
        (await Client(host, app.Token).GetAsync(Probe.Read)).Dispose();
        var third = await LastUsedAsync(host, user);

        Assert.True(first is not null, "a request did not stamp last used");
        Assert.True(second == first, "last used was stamped again within the interval");
        Assert.True(third > backdated, "last used was not stamped once the interval passed");
    }

    // #799 — approving an app is audited where the approval spends the password: a
    // first approval and a wider one each create an authorization and read Connected;
    // one that reuses an authorization already covering the request reads Reconnected.
    [Fact]
    public async Task Approval_IsAudited_AsAConnection()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);

        Assert.Equal([(AuditActions.UserAppConnected, user.Id, app.ClientId, ReadScope)], await ConnectionAuditAsync(user));
    }

    [Fact]
    public async Task WiderApproval_IsAudited_AsAConnection()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);
        await ConnectAsync(host, user, [ReadScope, WriteScope], app.ClientId);

        var rows = await ConnectionAuditAsync(user);
        Assert.True(rows.Count == 2 && rows[1].Action == AuditActions.UserAppConnected,
            $"a wider approval was not recorded as a connection: {string.Join(", ", rows)}");
        Assert.Equal($"{ReadScope} {WriteScope}", rows[1].Scopes);
    }

    [Fact]
    public async Task Reconnect_IsAudited_AsAReconnection()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(host, user, ReadScope);
        await ConnectAsync(host, user, [ReadScope], app.ClientId);

        Assert.Equal(
            [(AuditActions.UserAppConnected, user.Id, app.ClientId, ReadScope),
             (AuditActions.UserAppReconnected, user.Id, app.ClientId, ReadScope)],
            await ConnectionAuditAsync(user));
    }

    // #1148 — the app to disconnect travels in the query, so the idempotency fingerprint
    // must cover it: one key reused for another app is a conflict, never a replay of the
    // first app's 204 that would leave the second app connected.
    [Fact]
    public async Task Disconnect_ReusingAKeyForAnotherApp_IsAConflict_NotAReplay()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var first = await ConnectAsync(host, user, ReadScope);
        var second = await ConnectAsync(host, user, ReadScope);
        var key = Guid.NewGuid().ToString();

        using var disconnected = await DisconnectAsync(host, user.Jwt, $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(first.ClientId)}", key);
        using var retried = await DisconnectAsync(host, user.Jwt, $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(first.ClientId)}", key);
        using var reused = await DisconnectAsync(host, user.Jwt, $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(second.ClientId)}", key);
        using var stillConnected = await Client(host, second.Token).GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillConnected.StatusCode);
    }

    private Task<List<(string Action, Guid Actor, string ClientId, string Scopes)>> ConnectionAuditAsync(SeededUser user) =>
        factory.WithTenantScopeAsync(user.AccountId, async db => (await db.AuditEvents
            .Where(e => e.EntityId == user.Id
                && (e.Action == AuditActions.UserAppConnected || e.Action == AuditActions.UserAppReconnected))
            .OrderBy(e => EF.Property<long>(e, "Sequence"))
            .Select(e => new { e.Action, e.ActorUserId, e.DetailsJson })
            .ToListAsync())
            .Select(e =>
            {
                var details = JsonDocument.Parse(e.DetailsJson!).RootElement;
                return (e.Action, e.ActorUserId, details.GetProperty("clientId").GetString()!,
                    string.Join(' ', details.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString())));
            })
            .ToList());

    private sealed record Connection(string ClientId, string Token);

    private static Task<Connection> ConnectAsync(WebApplicationFactory<Program> host, SeededUser user, string scope) =>
        ConnectAsync(host, user, [scope]);

    private static async Task<Connection> ConnectAsync(
        WebApplicationFactory<Program> host, SeededUser user, string[] scopes, string? clientId = null)
    {
        clientId ??= await RegisterClientAsync(host.Services);
        var verifier = NewVerifier();
        var code = await AuthorizeAsync(host, user.Jwt, clientId, verifier, scopes);
        using var response = await RedeemAsync(host, clientId, code, verifier);
        response.EnsureSuccessStatusCode();
        return new(clientId, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!);
    }

    private async Task<SeededUser> SeedOnFarmAsync(Guid accountId, string role)
    {
        var email = $"oauth-user-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role);
        var id = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return new(id, accountId, await factory.LoginForAccessTokenAsync(email), "");
    }

    private static async Task<HttpResponseMessage> DisconnectAsync(
        WebApplicationFactory<Program> host, string jwt, string path, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return await Client(host, jwt).SendAsync(request);
    }

    private Task<(int Authorizations, int Tokens)> StillValidAsync(SeededUser user) =>
        factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            var subject = user.Id.ToString();
            return (
                await db.OAuthAuthorizations.CountAsync(a => a.Subject == subject && a.Status == Statuses.Valid),
                await db.OAuthTokens.CountAsync(t => t.Subject == subject && t.Status == Statuses.Valid));
        });

    private static async Task<DateTimeOffset?> LastUsedAsync(WebApplicationFactory<Program> host, SeededUser user)
    {
        var apps = await Client(host, user.Jwt).GetFromJsonAsync<JsonElement>("/api/v1/me/connected-apps");
        var lastUsed = apps.EnumerateArray().Single().GetProperty("lastUsedAtUtc");
        return lastUsed.ValueKind == JsonValueKind.Null ? null : lastUsed.GetDateTimeOffset();
    }
}
