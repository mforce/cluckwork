using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Cluckwork.Api.IntegrationTests;

// #1146 — the farm's "Allow connected apps" switch. Off refuses consent and every OAuth
// token of the farm on its next request; it revokes nothing, so back on restores them.
public sealed partial class OAuthFailClosedTests
{
    private const string SettingsPath = "/api/v1/account/settings";

    [Fact]
    public async Task ConnectedApps_AreOnByDefault_ForNewAndExistingFarms()
    {
        var user = await SeedAsync(Roles.Manager);
        var account = await factory.CreateAuthedClient(user.Jwt).GetFromJsonAsync<JsonElement>("/api/v1/account");
        Assert.True(account.GetProperty("allowConnectedApps").GetBoolean(), "a new farm starts with connected apps off");

        // The base account predates the column, so the migration's default decided it.
        var existing = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, db =>
            db.Accounts.Where(a => a.Id == SeedDefaults.AccountId).Select(a => a.AllowConnectedApps).SingleAsync());
        Assert.True(existing, "the migration turned connected apps off for an existing farm");
    }

    [Fact]
    public async Task ConnectedAppsSwitch_IsOwnerOnly()
    {
        var user = await SeedAsync(Roles.Manager);
        var account = await ReadAccountAsync(user.Jwt);

        using var refused = await PutSwitchAsync(user.Jwt, allow: false, account["version"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True((await ReadAccountAsync(user.Jwt))["allowConnectedApps"]!.GetValue<bool>(),
            "a Manager turned connected apps off");
    }

    [Fact]
    public async Task ConnectedAppsOff_RefusesAnExistingToken_OnTheNextRequest()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope);

        await SwitchConnectedAppsAsync(user, on: false);

        await AssertRefusedAsync(host, token, "Auth.ConnectedAppsOff");
    }

    // Off refuses; only Disconnect revokes. A connection nobody disconnected works again.
    [Fact]
    public async Task ConnectedAppsBackOn_RestoresTheConnection()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope);
        await SwitchConnectedAppsAsync(user, on: false);
        await AssertRefusedAsync(host, token, "Auth.ConnectedAppsOff");

        await SwitchConnectedAppsAsync(user, on: true);

        using var response = await Client(host, token).GetAsync(Probe.Read);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The session is not a connected app: the person keeps using the farm.
    [Fact]
    public async Task ConnectedAppsOff_LeavesSessionsAlone()
    {
        var user = await SeedAsync(Roles.Manager);
        await SwitchConnectedAppsAsync(user, on: false);

        using var response = await factory.CreateAuthedClient(user.Jwt).GetAsync("/api/v1/account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConnectedAppsOff_RefusesConsent_BeforeThePassword_AndIssuesNoCode()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var clientId = await RegisterClientAsync(host.Services);
        await SwitchConnectedAppsAsync(user, on: false);

        using var asked = await Client(host, user.Jwt).GetAsync(AuthorizeUri(clientId, NewVerifier(), ReadScope));
        var approve = new HttpRequestMessage(HttpMethod.Get, AuthorizeUri(clientId, NewVerifier(), ReadScope));
        approve.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(user.Jwt));
        using var approved = await Client(host, user.Jwt).SendAsync(approve);

        Assert.Equal(HttpStatusCode.Forbidden, asked.StatusCode);
        Assert.Equal("Auth.ConnectedAppsOff", await TitleOf(asked));
        Assert.Equal(HttpStatusCode.Forbidden, approved.StatusCode);
        Assert.Equal("Auth.ConnectedAppsOff", await TitleOf(approved));
        await using var scope = host.Services.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        await foreach (var _ in authorizations.FindBySubjectAsync(user.Id.ToString()))
            Assert.Fail("consent stored an authorization while connected apps were off");
    }

    // An app registers before anyone signs in, so there is no farm to ask (#797).
    [Fact]
    public async Task ConnectedAppsOff_LeavesRegistrationAlone()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        await SwitchConnectedAppsAsync(user, on: false);

        using var registered = await Client(host, bearer: null).PostAsJsonAsync(
            "/api/v1/oauth/register", new { redirect_uris = new[] { RedirectUri } });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
    }

    [Fact]
    public async Task ConnectedAppsSwitch_IsAudited_WithTheOwnerAsActor()
    {
        var user = await SeedAsync(Roles.Manager);
        await SwitchConnectedAppsAsync(user, on: false);

        var recorded = await factory.WithTenantScopeAsync(user.AccountId, db => db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditActions.AccountUpdateSettings)
            .SingleAsync());

        Assert.Equal(user.OwnerEmail, recorded.ActorEmail);
        var details = JsonNode.Parse(recorded.DetailsJson!)!;
        Assert.Equal("true", AllowConnectedAppsIn(details["before"]!));
        Assert.Equal("false", AllowConnectedAppsIn(details["after"]!));
    }

    // A Farm settings save racing the switch at the same Version: one wins whole, the
    // other gets 409, and the stored row is never a blend of the two. The two writes go
    // through different endpoints, so only the shared Version token can catch this.
    [Fact]
    public async Task ConnectedAppsSwitch_RacingASettingsSave_ExactlyOneWins()
    {
        var user = await SeedAsync(Roles.Manager);
        var owner = await factory.LoginForAccessTokenAsync(user.OwnerEmail);
        var settings = await ReadSettingsAsync(owner);
        var version = settings["settings"]!["version"]!.GetValue<int>();

        var results = await Task.WhenAll(
            PutSwitchAsync(owner, allow: false, version),
            PutSettingsAsync(owner, SettingsBody(settings, name: "Renamed in the race")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var after = (await ReadSettingsAsync(owner))["settings"]!;
        Assert.Equal(version + 1, after["version"]!.GetValue<int>());
        var switchWon = !after["allowConnectedApps"]!.GetValue<bool>();
        var renameWon = after["name"]!.GetValue<string>() == "Renamed in the race";
        Assert.True(switchWon ^ renameWon, $"the race stored {after.ToJsonString()}");
    }

    // Two Owners turning it off from the same page load: the second holds a stale
    // Version, so it gets 409 whichever request lands first.
    [Fact]
    public async Task ConnectedAppsSwitch_TwoOwnersAtOneVersion_ExactlyOneWins()
    {
        var user = await SeedAsync(Roles.Manager);
        var owner = await factory.LoginForAccessTokenAsync(user.OwnerEmail);
        var version = (await ReadAccountAsync(owner))["version"]!.GetValue<int>();

        var results = await Task.WhenAll(PutSwitchAsync(owner, allow: false, version), PutSwitchAsync(owner, allow: false, version));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(version + 1, (await ReadAccountAsync(owner))["version"]!.GetValue<int>());
    }

    private async Task SwitchConnectedAppsAsync(SeededUser user, bool on)
    {
        var owner = await factory.LoginForAccessTokenAsync(user.OwnerEmail);
        var version = (await ReadAccountAsync(owner))["version"]!.GetValue<int>();
        using var response = await PutSwitchAsync(owner, on, version);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<JsonNode> ReadAccountAsync(string jwt) =>
        (await factory.CreateAuthedClient(jwt).GetFromJsonAsync<JsonNode>("/api/v1/account"))!;

    private Task<HttpResponseMessage> PutSwitchAsync(string jwt, bool allow, int version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/account/connected-apps")
        {
            Content = JsonContent.Create(new { allow, version }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return factory.CreateAuthedClient(jwt).SendAsync(request);
    }

    private async Task<JsonNode> ReadSettingsAsync(string jwt) =>
        (await factory.CreateAuthedClient(jwt).GetFromJsonAsync<JsonNode>(SettingsPath))!;

    private Task<HttpResponseMessage> PutSettingsAsync(string jwt, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, SettingsPath) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return factory.CreateAuthedClient(jwt).SendAsync(request);
    }

    // The whole block as the server holds it, so a save changes only what the test names.
    private static JsonObject SettingsBody(JsonNode settings, string? name = null)
    {
        var current = settings["settings"]!;
        return new JsonObject
        {
            ["name"] = name ?? current["name"]!.GetValue<string>(),
            ["timeZoneId"] = current["timeZoneId"]!.DeepClone(),
            ["locale"] = current["locale"]!.DeepClone(),
            ["currencyCode"] = current["currencyCode"]!.DeepClone(),
            ["unitSystem"] = current["unitSystem"]!.DeepClone(),
            ["firstDayOfWeek"] = current["firstDayOfWeek"]?.DeepClone(),
            ["dateFormatOverride"] = current["dateFormatOverride"]?.DeepClone(),
            ["timeFormatOverride"] = current["timeFormatOverride"]?.DeepClone(),
            ["brand"] = current["brand"]!.DeepClone(),
            ["defaultStepperUnit"] = current["defaultStepperUnit"]!.DeepClone(),
            ["workerSaleAllocationPolicy"] = settings["workerSaleAllocationPolicy"]!.DeepClone(),
            ["maxDiscountPercent"] = settings["maxDiscountPercent"]?.DeepClone(),
            ["version"] = current["version"]!.DeepClone(),
        };
    }

    private async Task<string> StepUpAsync(string jwt)
    {
        using var response = await factory.CreateAuthedClient(jwt)
            .PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static string? AllowConnectedAppsIn(JsonNode snapshot) =>
        snapshot.AsObject().FirstOrDefault(p => string.Equals(p.Key, "AllowConnectedApps", StringComparison.OrdinalIgnoreCase))
            .Value?.ToJsonString();
}
