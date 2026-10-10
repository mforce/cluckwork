using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #806 — /mcp behind the real pipeline, driven by the SDK's own client where a session
// matters. Production has no tools yet, so the test host adds ProbeTools: one read tool and
// one write tool, authorized the way a real tool is (a scope policy, and a role policy).
[Collection(IntegrationCollection.Name)]
public sealed class McpEndpointTests(CluckworkWebApplicationFactory factory, ITestOutputHelper output)
{
    private const string Resource = "https://localhost/mcp";
    private const string MetadataUrl = "https://localhost/.well-known/oauth-protected-resource/mcp";
    private const string OtherResource = "https://other.example/api";
    private const string OtherScope = "cw806.other";
    private const string Initialize = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"cw806","version":"1"}}}
        """;

    [Fact]
    public async Task NoToken_GetsAChallengeNamingTheResourceMetadata()
    {
        using var host = Host();

        using var response = await PostAsync(host, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{MetadataUrl}\"", Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    // The client follows the challenge to the metadata, and the metadata to the issuer's
    // discovery document, whose issuer must match it byte for byte.
    [Fact]
    public async Task Metadata_LeadsToTheAuthorizationServer()
    {
        using var host = Host();
        using var client = OAuthServerTests.HttpsClient(host, bearer: null);

        var metadata = await client.GetFromJsonAsync<JsonElement>(MetadataUrl);
        var issuer = Assert.Single(metadata.GetProperty("authorization_servers").EnumerateArray()).GetString();
        var discovery = await client.GetFromJsonAsync<JsonElement>(
            new Uri(new Uri(issuer!), ".well-known/oauth-authorization-server"));

        Assert.Equal(Resource, metadata.GetProperty("resource").GetString());
        Assert.Equal(OAuthScopes.All, metadata.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(issuer, discovery.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task SessionJwt_IsRefused()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);

        using var response = await PostAsync(host, user.Jwt);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // One endpoint serves both revisions #789 targets. The SDK's client sends no
    // Idempotency-Key, and the tool body resolves McpCallContext in the request's scope.
    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task BothRevisions_ListAndCallATool(string? protocolVersion)
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);
        var counter = new RequestCounter();

        await using (var client = await McpAsync(host, token, protocolVersion, counter))
        {
            var tools = await client.ListToolsAsync();
            var result = await client.CallToolAsync(ProbeTools.Read);

            Assert.Contains(tools, tool => tool.Name == ProbeTools.Read);
            Assert.Equal($"{user.Id} {user.AccountId}", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }

        output.WriteLine($"{protocolVersion ?? "latest"}: {counter.Count} HTTP requests to connect, list and call one tool");
    }

    // Stacked [Authorize] attributes: a tool is listed only for role ∩ scope.
    [Theory]
    [InlineData(Roles.Manager, OAuthScopes.ReadFarm, new[] { ProbeTools.Read })]
    [InlineData(Roles.Manager, OAuthScopes.WriteDailyEntries, new[] { ProbeTools.Write })]
    [InlineData(Roles.Manager, OAuthScopes.ReadFarm + " " + OAuthScopes.WriteDailyEntries, new[] { ProbeTools.Read, ProbeTools.Write })]
    [InlineData(Roles.ReadOnly, OAuthScopes.ReadFarm + " " + OAuthScopes.WriteDailyEntries, new[] { ProbeTools.Read })]
    public async Task ToolsList_IsFilteredByRoleAndScope(string role, string scope, string[] expected)
    {
        using var host = Host();
        var user = await SeedAsync(role);
        var token = await ConnectAsync(host, user.Jwt, scope);

        await using var client = await McpAsync(host, token);
        var tools = await client.ListToolsAsync();

        Assert.Equal(expected, tools.Select(tool => tool.Name).Order());
    }

    [Fact]
    public async Task UnlistedTool_IsRefusedWhenCalled()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);

        await using var client = await McpAsync(host, token);
        var refusal = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(ProbeTools.Write).AsTask());

        Assert.Equal(McpErrorCode.InvalidRequest, refusal.ErrorCode);
    }

    // RFC 8707: a client that names no resource still gets one, the only one there is.
    [Fact]
    public async Task TokenRequestedWithoutAResource_IsBoundToMcp()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm, resource: null);

        await using var client = await McpAsync(host, token);
        Assert.Contains(await client.ListToolsAsync(), tool => tool.Name == ProbeTools.Read);
    }

    [Fact]
    public async Task AuthorizationRequest_ForAnotherResource_IsRefused()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var clientId = await OAuthServerTests.RegisterClientAsync(host.Services);
        var query = OAuthServerTests.AuthorizeQuery(clientId, OAuthServerTests.NewCodeVerifier());
        query["resource"] = OtherResource;

        using var response = await OAuthServerTests.SendAuthorizeAsync(host, user.Jwt, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"error:{Errors.InvalidTarget}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TokenForAnotherResource_IsRefused()
    {
        using var host = Host(server => server.Resources.Add(new Uri(OtherResource)));
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm, OtherResource);

        using var response = await PostAsync(host, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithNoAudience_IsRefused()
    {
        using var host = Host(server => server.Handlers.Add(AfterResourcesAreSet(principal => principal.SetResources([]))));
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);

        using var response = await PostAsync(host, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The route's own gate. Real tokens always hold farm:read or daily-entries:write,
    // so the test host registers a third scope.
    [Fact]
    public async Task TokenWithNoMcpScope_GetsInsufficientScope()
    {
        using var host = Host(server => server.Scopes.Add(OtherScope));
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OtherScope);

        using var response = await PostAsync(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"{OAuthScopes.ReadFarm}\", resource_metadata=\"{MetadataUrl}\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    // Disconnect: the next request is challenged, so the client knows to reconnect.
    [Fact]
    public async Task RevokedToken_IsChallenged()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            await foreach (var authorization in authorizations.FindBySubjectAsync(user.Id.ToString()))
                Assert.True(await authorizations.TryRevokeAsync(authorization), "the authorization was not revoked");
        }

        using var response = await PostAsync(host, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(MetadataUrl, response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ConnectedAppsOff_IsRefused()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);

        await factory.WithTenantScopeAsync(user.AccountId, db => db.Accounts.Where(a => a.Id == user.AccountId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.AllowConnectedApps, false)));

        using var response = await PostAsync(host, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.ConnectedAppsOff", await TitleOf(response));
    }

    // Consent refuses a user with a pending password change, so no real token carries the
    // claim; the test host adds it to prove the gate covers /mcp too.
    [Fact]
    public async Task MustChangePassword_IsRefused()
    {
        using var host = Host(server => server.Handlers.Add(AfterResourcesAreSet(principal =>
        {
            principal.SetClaim("must_change_password", "true");
            principal.SetDestinations(static _ => [Destinations.AccessToken]);
        })));
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);

        using var response = await PostAsync(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.MustChangePassword", await TitleOf(response));
    }

    [Fact]
    public async Task RateLimitRefusal_IsAJsonRpcError()
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:OAuthApi:PermitLimit", "1");
            builder.UseSetting("RateLimiting:OAuthApi:WindowSeconds", "86400");
        });
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);

        using var first = await PostAsync(host, token);
        using var second = await PostAsync(host, token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2.0", body.GetProperty("jsonrpc").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("id").ValueKind);
        Assert.Contains("this connection", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task OversizedBody_IsRefused()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await ConnectAsync(host, user.Jwt, OAuthScopes.ReadFarm);
        var padding = new string(' ', 64 * 1024);

        using var response = await PostAsync(host, token, Initialize + padding);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // Design row 17: the idempotency exemption is metadata, and only /mcp carries it.
    [Fact]
    public void OnlyTheMcpPost_CarriesTheMcpMarker()
    {
        var marked = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<McpEndpoint>() is not null)
            .Select(endpoint => $"{string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} {endpoint.RoutePattern.RawText}");

        Assert.Equal(["POST /mcp/"], marked);
    }

    // Design rows 8, 9 and 12. Stateless keeps a tool in the request's scope (#805);
    // ConfigureSessionOptions is the one hook that runs after the SDK's stateless setup.
    // The idle-session service is registered regardless and must never start its timer.
    [Fact]
    public void Transport_IsStatelessAndKeepsNoSessions()
    {
        var transport = factory.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
        var idleTracking = Assert.Single(factory.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "IdleTrackingBackgroundService");

        Assert.Equal(HttpServerSessionMode.Stateless, transport.SessionMode);
        Assert.Null(transport.ConfigureSessionOptions);
        Assert.Null(Assert.IsAssignableFrom<BackgroundService>(idleTracking).ExecuteTask);
    }

    private sealed record SeededUser(Guid Id, Guid AccountId, string Jwt);

    private async Task<SeededUser> SeedAsync(string? role)
    {
        var email = $"mcp-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync($"mcp-owner-{Guid.NewGuid():N}@test.local");
        await factory.SeedUserAsync(accountId, email, role);
        var id = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return new(id, accountId, await factory.LoginForAccessTokenAsync(email));
    }

    // A connection the way an MCP client makes one: authorize, then redeem, both naming
    // the resource.
    private static async Task<string> ConnectAsync(
        WebApplicationFactory<Program> host, string jwt, string scope, string? resource = Resource)
    {
        var clientId = await OAuthServerTests.RegisterClientAsync(host.Services);
        var verifier = OAuthServerTests.NewCodeVerifier();
        var query = OAuthServerTests.AuthorizeQuery(clientId, verifier);
        query["scope"] = scope;
        var redeem = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.AuthorizationCode,
            ["client_id"] = clientId,
            ["redirect_uri"] = OAuthServerTests.RedirectUri,
            ["code_verifier"] = verifier,
        };
        if (resource is not null)
            query["resource"] = redeem["resource"] = resource;

        var location = await OAuthServerTests.ApproveAsync(host, jwt, query);
        redeem["code"] = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        using var response = await OAuthServerTests.HttpsClient(host, bearer: null)
            .PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(redeem));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private static Task<McpClient> McpAsync(
        WebApplicationFactory<Program> host, string token, string? protocolVersion = null, RequestCounter? counter = null)
    {
        var http = host.CreateDefaultClient(new Uri("https://localhost"), counter ?? new RequestCounter());
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(Resource) }, http, ownsHttpClient: true),
            new McpClientOptions { ProtocolVersion = protocolVersion });
    }

    private static Task<HttpResponseMessage> PostAsync(
        WebApplicationFactory<Program> host, string? token, string body = Initialize)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        return OAuthServerTests.HttpsClient(host, token).SendAsync(request);
    }

    private static async Task<string?> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    // Runs after the server stamps a code's resources, before the code is written.
    private static OpenIddictServerHandlerDescriptor AfterResourcesAreSet(Action<System.Security.Claims.ClaimsPrincipal> change) =>
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseInlineHandler(context =>
            {
                if (context.EndpointType is OpenIddictServerEndpointType.Authorization)
                    change(context.Principal!);
                return default;
            })
            .SetOrder(OpenIddictServerHandlers.InferResources.Descriptor.Order + 600)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private WebApplicationFactory<Program> Host(Action<OpenIddictServerOptions>? server = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddMcpServer().WithTools<ProbeTools>();
            if (server is not null)
                services.Configure(server);
        }));

    private sealed class RequestCounter : DelegatingHandler
    {
        private int _count;

        public int Count => _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [McpServerToolType]
    internal sealed class ProbeTools(McpCallContext call)
    {
        public const string Read = "probe_read";
        public const string Write = "probe_write";

        [McpServerTool(Name = Read, ReadOnly = true)]
        [Authorize(Policy = OAuthScopes.ReadFarm)]
        public string ReadCaller() => $"{call.UserId} {call.AccountId}";

        [McpServerTool(Name = Write)]
        [Authorize(Policy = OAuthScopes.WriteDailyEntries)]
        [Authorize(Policy = AuthPolicies.ProductionWrite)]
        public string WriteNothing() => call.ConnectedApp.ClientId;
    }
}
