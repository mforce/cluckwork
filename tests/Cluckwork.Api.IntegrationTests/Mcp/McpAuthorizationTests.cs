using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using static Cluckwork.Api.IntegrationTests.Mcp.McpConnection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #806 — who reaches /mcp and what they see: the challenge and discovery a client needs
// to connect, the token audience, role ∩ scope on tools/list and tools/call, and the
// shared request checks driven through the real route rather than a probe.
[Collection(IntegrationCollection.Name)]
public sealed class McpAuthorizationTests(CluckworkWebApplicationFactory factory)
{
    private static readonly string[] BothScopes = [OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries];

    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    public async Task McpClient_ConnectsAndCallsATool_OnBothProtocolRevisions(string protocolVersion)
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var token = await IssueAsync(host, user, [OAuthScopes.ReadFarm]);

        await using var client = await ConnectAsync(host, token, protocolVersion);
        var result = await client.CallToolAsync(ProbeTools.Read);

        Assert.Equal(protocolVersion, client.NegotiatedProtocolVersion);
        var caller = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.StartsWith($"{user.AccountId}/{user.Id}/", caller);
    }

    // Role ∩ scope, by composition: a tool is listed only when both policies pass.
    [Theory]
    [InlineData(Roles.Manager, new[] { OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries }, new[] { ProbeTools.Read, ProbeTools.Write })]
    [InlineData(Roles.Manager, new[] { OAuthScopes.ReadFarm }, new[] { ProbeTools.Read })]
    [InlineData(Roles.Manager, new[] { OAuthScopes.WriteDailyEntries }, new[] { ProbeTools.Write })]
    [InlineData(Roles.ReadOnly, new[] { OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries }, new[] { ProbeTools.Read })]
    [InlineData(Roles.Sales, new[] { OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries }, new[] { ProbeTools.Read })]
    public async Task ToolsList_ShowsOnlyToolsTheRoleAndScopesAllow(string role, string[] scopes, string[] expected)
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, role), scopes);

        await using var client = await ConnectAsync(host, token);
        var tools = await client.ListToolsAsync();

        Assert.Equal(expected.Order(), tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task HiddenTool_IsRefused_WhenCalledAnyway()
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OAuthScopes.ReadFarm]);

        await using var client = await ConnectAsync(host, token);
        var refused = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(ProbeTools.Write).AsTask());

        Assert.Equal(McpErrorCode.InvalidRequest, refused.ErrorCode);
    }

    // RFC 9728 §5.1: a client with no token learns where to authenticate from the challenge.
    [Fact]
    public async Task NoToken_Gets401_NamingTheProtectedResourceMetadata()
    {
        using var host = Host(factory);

        using var response = await PostAsync(host, bearer: null, Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{ResourceMetadata}\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ProtectedResourceMetadata_NamesTheIssuerAndTheScopes()
    {
        using var host = Host(factory);

        var metadata = await OAuthServerTests.HttpsClient(host, bearer: null).GetFromJsonAsync<JsonElement>(ResourceMetadata);

        Assert.Equal(McpUrl, metadata.GetProperty("resource").GetString());
        Assert.Equal(["https://localhost/"], metadata.GetProperty("authorization_servers").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(BothScopes, metadata.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()));
    }

    // The metadata URL comes from OAuth:Issuer, so a request arriving under another Host
    // is still pointed at the issuer's (#538).
    [Fact]
    public async Task Challenge_NamesTheIssuer_NotTheRequestHost()
    {
        using var host = Host(factory).WithWebHostBuilder(builder => builder.UseSetting("AllowedHosts", "*"));
        var request = new HttpRequestMessage(HttpMethod.Post, "https://internal.example/mcp")
        {
            Content = new StringContent(Initialize, System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await host.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ResourceMetadata, response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task TokenWithoutTheMcpAudience_IsRefused()
    {
        // A token minted before #806 carries no audience at all.
        using var host = Host(factory, alterIssued: principal => principal.SetResources(Array.Empty<string>()));
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OAuthScopes.ReadFarm]);

        using var response = await PostAsync(host, token, Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenIssuedForAnotherResource_IsRefused()
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OAuthScopes.ReadFarm], OtherResource);

        using var response = await PostAsync(host, token, Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The positive half of the two refusals above: naming /mcp, or no resource at all,
    // yields a token /mcp accepts.
    [Theory]
    [InlineData(McpUrl)]
    [InlineData(null)]
    public async Task TokenForMcp_IsAccepted(string? resource)
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OAuthScopes.ReadFarm], resource);

        using var response = await PostAsync(host, token, Initialize);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnregisteredResource_IsRefused_AtAuthorizeAndAtTheTokenEndpoint()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var clientId = await OAuthServerTests.RegisterClientAsync(host.Services);
        const string elsewhere = "https://elsewhere.example/api";

        using var denied = await OAuthServerTests.SendAuthorizeAsync(
            host, user.Jwt, AuthorizeQuery(clientId, Guid.NewGuid().ToString("N"), [OAuthScopes.ReadFarm], elsewhere));
        using var redeemed = await RedeemAsync(host, clientId, "cw806-code", "cw806-verifier", "https://client.example/callback", elsewhere);

        // Refused before the redirect URI is trusted, so OpenIddict answers directly.
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains($"error:{OpenIddictConstants.Errors.InvalidTarget}", await denied.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, redeemed.StatusCode);
        Assert.Equal(OpenIddictConstants.Errors.InvalidTarget,
            (await redeemed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // RFC 6750 §3.1: the token holds neither MCP scope, so the client is told which to ask for.
    [Fact]
    public async Task TokenWithoutAnMcpScope_Gets403InsufficientScope()
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OtherScope]);

        using var response = await PostAsync(host, token, Initialize);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"farm:read daily-entries:write\", resource_metadata=\"{ResourceMetadata}\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task SessionJwt_IsRefused()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);

        using var response = await PostAsync(host, user.Jwt, Initialize);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The shared request checks (#796, #1146) through /mcp itself.
    [Fact]
    public async Task FarmSwitchOff_RefusesTheConnection_OnTheNextRequest()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var token = await IssueAsync(host, user, [OAuthScopes.ReadFarm]);
        await AssertAcceptedAsync(host, token);

        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(user.OwnerEmail));
        var version = (await owner.GetFromJsonAsync<JsonNode>("/api/v1/account"))!["version"]!.GetValue<int>();
        using var off = new HttpRequestMessage(HttpMethod.Put, "/api/v1/account/connected-apps")
        {
            Content = JsonContent.Create(new { allow = false, version }),
        };
        off.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(off)).StatusCode);

        await AssertRefusedAsync(host, token, "Auth.ConnectedAppsOff");
    }

    // Each shared check, tripped after the token was issued, refuses the next request.
    [Theory]
    [InlineData("Auth.AccountDisabled")]
    [InlineData("Auth.FarmSuspended")]
    [InlineData("Auth.CredentialsSuperseded")]
    public async Task SharedCheck_RefusesTheConnection_WithAChallenge(string title)
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var token = await IssueAsync(host, user, [OAuthScopes.ReadFarm]);
        await AssertAcceptedAsync(host, token);

        await factory.WithTenantScopeAsync(user.AccountId, db => title switch
        {
            "Auth.AccountDisabled" => db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, DateTimeOffset.UtcNow)),
            "Auth.FarmSuspended" => db.Accounts.Where(a => a.Id == user.AccountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false)),
            _ => db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.CredentialEpoch, u => u.CredentialEpoch + 1)),
        });

        await AssertRefusedAsync(host, token, title);
    }

    // #388 through /mcp: a Worker's tool call sees exactly the flocks assigned to them.
    [Fact]
    public async Task AssignedWorker_ToolSeesOnlyTheirFlocks()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, role: null);
        var assigned = await factory.SeedFlockAsync(user.AccountId, Guid.NewGuid());
        await factory.SeedFlockAsync(user.AccountId, Guid.NewGuid());
        await factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(Guid.NewGuid(), user.AccountId, user.Id, null, null, assigned));
            await db.SaveChangesAsync();
        });
        var token = await IssueAsync(host, user, [OAuthScopes.ReadFarm]);

        await using var client = await ConnectAsync(host, token);
        var caller = Assert.IsType<TextContentBlock>(Assert.Single((await client.CallToolAsync(ProbeTools.Read)).Content)).Text;

        Assert.EndsWith($"/True/{assigned}", caller);
    }

    // What a client does with no configuration: follow the challenge to the resource
    // metadata, and that to the authorization server's own discovery document.
    [Fact]
    public async Task Challenge_LeadsToTheAuthorizationServer()
    {
        using var host = Host(factory);
        using var http = OAuthServerTests.HttpsClient(host, bearer: null);

        using var challenge = await PostAsync(host, bearer: null, Initialize);
        var metadataUrl = challenge.Headers.WwwAuthenticate.Single().Parameter!.Split('"')[1];
        var metadata = await http.GetFromJsonAsync<JsonElement>(metadataUrl);
        var issuer = Assert.Single(metadata.GetProperty("authorization_servers").EnumerateArray()).GetString()!;
        var discovery = await http.GetFromJsonAsync<JsonElement>(new Uri(new Uri(issuer), ".well-known/oauth-authorization-server"));

        Assert.Equal(ResourceMetadata, metadataUrl);
        Assert.Equal("https://localhost/", issuer);
        Assert.Equal(issuer, discovery.GetProperty("issuer").GetString());
        Assert.Equal("https://localhost/api/v1/oauth/authorize", discovery.GetProperty("authorization_endpoint").GetString());
    }

    [Fact]
    public async Task Disconnect_RefusesTheConnection_OnTheNextRequest()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var token = await IssueAsync(host, user, [OAuthScopes.ReadFarm]);
        await AssertAcceptedAsync(host, token);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            await foreach (var authorization in authorizations.FindBySubjectAsync(user.Id.ToString()))
                Assert.True(await authorizations.TryRevokeAsync(authorization), "the authorization was not revoked");
        }

        using var response = await PostAsync(host, token, Initialize);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ResourceMetadata, response.Headers.WwwAuthenticate.ToString());
    }

    // Consent refuses a user with a pending password change (OAuthFailClosedTests), so no
    // real token carries one; this one stands in to prove /mcp is not on the allowed list.
    [Fact]
    public async Task PendingPasswordChange_IsRefused()
    {
        using var host = Host(factory, alterIssued: principal =>
        {
            principal.SetClaim("must_change_password", "true");
            principal.SetDestinations(static _ => [OpenIddictConstants.Destinations.AccessToken]);
        });
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OAuthScopes.ReadFarm]);

        using var response = await PostAsync(host, token, Initialize);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.MustChangePassword",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    // #806 — an MCP client reads the 429 as a JSON-RPC error. The limiter runs before the
    // body is read, so the request id is unknown and null.
    [Fact]
    public async Task OverTheBudget_Gets429_AsAJsonRpcError()
    {
        using var host = Host(factory, limits: ("OAuthApi", 1));
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OAuthScopes.ReadFarm]);
        await AssertAcceptedAsync(host, token);

        using var response = await PostAsync(host, token, Initialize);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero, "the 429 carries no Retry-After");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var isRpcError = body.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0"
            && body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Null
            && body.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out _)
            && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString());
        Assert.True(isRpcError, $"the 429 body is not a JSON-RPC error: {body}");
    }

    private static async Task AssertAcceptedAsync(WebApplicationFactory<Program> host, string token)
    {
        using var response = await PostAsync(host, token, Initialize);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The problem body names the check; the challenge tells the client to re-authorize.
    private static async Task AssertRefusedAsync(WebApplicationFactory<Program> host, string token, string title)
    {
        using var response = await PostAsync(host, token, Initialize);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(title, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal($"Bearer error=\"invalid_token\", resource_metadata=\"{ResourceMetadata}\"",
            response.Headers.WwwAuthenticate.ToString());
    }
}
