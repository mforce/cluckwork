using System.Net;
using System.Reflection;
using System.Text.Json;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Cluckwork.Api.Mcp;
using static Cluckwork.Api.IntegrationTests.Mcp.McpConnection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #806 guards rows 8-12, 17, 18, 29 and 30 (docs/plans/770-mcp-server/02-guards.md),
// read from the running application's endpoint table and services.
[Collection(IntegrationCollection.Name)]
public sealed class McpEndpointTests(CluckworkWebApplicationFactory factory)
{
    // Rows 10, 29, 30: the body marker BodyReadingEndpointTests demands, the #309 cap that
    // marker does not set, and the per-token budget and scope gate AcceptOAuthTokens adds.
    [Fact]
    public void McpPost_CarriesTheBodyCap_TheBudget_AndTheOAuthGate()
    {
        var mcp = McpPost(factory);

        Assert.Equal(CluckworkMcp.MaxRequestBodyBytes, mcp.Metadata.GetMetadata<MaxRequestBodyBytesMetadata>()?.Bytes);
        Assert.Equal(RateLimitingOptions.OAuthApiPolicyName, mcp.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.True(OAuthEndpoints.AcceptsOAuthTokens(mcp), "/mcp does not accept OAuth tokens");
        Assert.NotNull(mcp.Metadata.GetMetadata<ReadsRequestBodyAttribute>());
    }

    // Row 17: the exemption is endpoint metadata, and exactly one endpoint carries it.
    [Fact]
    public void IdempotencyExemption_IsCarriedByTheMcpPostAlone()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.True(endpoints.Count > 100, $"walked only {endpoints.Count} endpoints");
        var exempt = Assert.Single(endpoints, e => e.Metadata.GetMetadata<HandlesOwnIdempotencyAttribute>() is not null);
        Assert.Same(McpPost(factory), exempt);
    }

    // #1230: exactly the [McpServerTool] methods are served, and an optional parameter is
    // advertised as its plain type: omitted, never null.
    [Fact]
    public async Task ToolSchemas_AreServedForEveryTool_WithoutNull()
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Owner),
            [OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries]);
        await using var client = await ConnectAsync(host, token);

        var tools = await client.ListToolsAsync();

        // The test host also serves ProbeTools.
        var declared = typeof(McpCallContext).Assembly.GetTypes().Append(typeof(ProbeTools))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>().ToHashSet();
        Assert.True(declared.Count >= 11, $"the walk found {declared.Count} tools");
        Assert.Equal(declared.Order(), tools.Select(t => t.Name).Order());
        Assert.Empty(tools.SelectMany(t => NullKeywords(t.Name, t.ProtocolTool.InputSchema)));
    }

    private static IEnumerable<string> NullKeywords(string path, JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().SelectMany(p => OffersNull(p)
            ? [$"{path}.{p.Name}"]
            : NullKeywords($"{path}.{p.Name}", p.Value)),
        JsonValueKind.Array => node.EnumerateArray().SelectMany((e, i) => NullKeywords($"{path}[{i}]", e)),
        _ => [],
    };

    private static bool OffersNull(JsonProperty p) => (p.Name, p.Value.ValueKind) switch
    {
        ("type", JsonValueKind.String) => p.Value.GetString() == "null",
        ("type", JsonValueKind.Array) => p.Value.EnumerateArray().Any(t => t.GetString() == "null"),
        ("default", JsonValueKind.Null) => true,
        _ => false,
    };

    // Row 18: no MCP client sends an Idempotency-Key, and every MCP message is a POST.
    [Fact]
    public async Task McpMessages_WithoutAnIdempotencyKey_AreServed()
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [OAuthScopes.ReadFarm]);

        using var initialize = await PostAsync(host, token, Initialize);
        using var list = await PostAsync(host, token, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    // Rows 8 and 9. Under Stateless the SDK runs a tool in the request's own services;
    // ScopeRequests is deliberately not pinned, because the SDK forces it there.
    [Fact]
    public void Transport_IsStateless_WithNoSessionHook()
    {
        var transport = factory.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;

        Assert.Equal(HttpServerSessionMode.Stateless, transport.SessionMode);
        Assert.Null(transport.ConfigureSessionOptions);
    }

    // Row 12: WithHttpTransport registers the idle-session sweep unconditionally. Under
    // Stateless it never starts, so #271's blocker list does not grow. Inertness, not absence.
    [Fact]
    public void IdleSessionSweep_IsRegistered_ButNeverStarts()
    {
        _ = factory.Server;
        var sweep = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<BackgroundService>(),
            s => s.GetType().Name == "IdleTrackingBackgroundService");

        Assert.Null(sweep.ExecuteTask);
    }

    // Row 29: the cap refuses a declared-oversize body before authentication.
    [Fact]
    public async Task OversizedBody_IsRefused()
    {
        using var response = await PostAsync(factory, bearer: null, new string(' ', (int)CluckworkMcp.MaxRequestBodyBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // Stateless serves no GET stream, so the MCP spec wants 405. In Production the SPA
    // fallback would otherwise answer GET and HEAD with the app's HTML.
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task GetAndHead_Get405(string method)
    {
        using var response = await OAuthServerTests.HttpsClient(factory, bearer: null)
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), CluckworkMcp.Path));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["POST"], response.Content.Headers.Allow);
    }

    // The Origin check covers every request to /mcp and runs before authentication, so a
    // foreign page gets 403 with no token and on GET. Without Origin, the 401 and 405 stand.
    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    public async Task ForeignOrigin_Gets403_BeforeAuthentication_OnEveryMethod(string protocolVersion)
    {
        using var foreignPost = await SendAsync(HttpMethod.Post, protocolVersion, "https://untrusted.example");
        using var foreignGet = await SendAsync(HttpMethod.Get, protocolVersion, "https://untrusted.example");
        using var foreignDelete = await SendAsync(HttpMethod.Delete, protocolVersion, "https://untrusted.example");
        using var foreignTrailingSlash = await SendAsync(HttpMethod.Post, protocolVersion, "https://untrusted.example", "/MCP/");
        using var plainPost = await SendAsync(HttpMethod.Post, protocolVersion, origin: null);
        using var plainGet = await SendAsync(HttpMethod.Get, protocolVersion, origin: null);
        using var plainDelete = await SendAsync(HttpMethod.Delete, protocolVersion, origin: null);

        Assert.Equal(HttpStatusCode.Forbidden, foreignPost.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignGet.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignTrailingSlash.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, plainPost.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, plainGet.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, plainDelete.StatusCode);
    }

    [Theory]
    [InlineData("https://localhost/", new string[0], true)]
    [InlineData("https://localhost/", new[] { "https://localhost" }, true)]
    [InlineData("https://localhost/", new[] { "HTTPS://LOCALHOST" }, true)]
    [InlineData("https://localhost/", new[] { "https://localhost:443" }, true)]
    [InlineData("https://localhost:8496/", new[] { "https://localhost:8496" }, true)]
    [InlineData("https://bücher.example/", new[] { "https://xn--bcher-kva.example" }, true)]
    [InlineData("https://localhost/", new[] { "https://localhost", "" }, false)]
    [InlineData("https://localhost/", new[] { "https://localhost", "https://localhost" }, false)]
    [InlineData("https://localhost/", new[] { "" }, false)]
    [InlineData("https://localhost/", new[] { "null" }, false)]
    [InlineData("https://localhost/", new[] { "https://localhost/" }, false)]
    [InlineData("https://localhost/", new[] { "https://localhost/mcp" }, false)]
    [InlineData("https://localhost/", new[] { "https://localhost?x=1" }, false)]
    [InlineData("https://localhost/", new[] { "https://localhost#x" }, false)]
    [InlineData("https://localhost/", new[] { "https://user@localhost" }, false)]
    [InlineData("https://localhost/", new[] { "http://localhost" }, false)]
    [InlineData("https://localhost/", new[] { "https://localhost:8443" }, false)]
    [InlineData("https://localhost/", new[] { "https://untrusted.example" }, false)]
    public void RequiredOrigin_ComparesSchemeAsciiHostAndEffectivePort(string issuer, string[] sent, bool allowed) =>
        Assert.Equal(allowed, new CluckworkMcp.RequiredOrigin(new Uri(issuer)).Allows(new StringValues(sent)));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string protocolVersion, string? origin, string path = CluckworkMcp.Path)
    {
        var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
            request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return OAuthServerTests.HttpsClient(factory, bearer: null).SendAsync(request);
    }

    // The tool walks in McpToolSurfaceTests must reach every tool the SDK serves. The
    // probe host adds two tools, so a walk that finds nothing fails here today.
    [Fact]
    public void ToolWalk_ReachesEveryToolTheSdkRegisters()
    {
        var apiTypes = typeof(McpCallContext).Assembly.GetTypes();
        using var probed = Host(factory);
        var registered = probed.Services.GetServices<McpServerTool>().Count();

        Assert.Equal(factory.Services.GetServices<McpServerTool>().Count(), Walked(apiTypes));
        Assert.True(registered >= 2, $"the probe host registered {registered} tools");
        Assert.Equal(registered, Walked(apiTypes.Append(typeof(ProbeTools))));
    }

    // OAuth tokens are the only way in, so without the server that issues them there is no /mcp.
    [Fact]
    public void WithoutAnIssuer_McpIsNotMapped()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("OAuth:Issuer", ""));

        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        Assert.Contains(routes, r => r?.StartsWith("/api/v1/flocks", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(routes, r => r?.TrimEnd('/') == CluckworkMcp.Path);
    }

    private static int Walked(IEnumerable<Type> types) =>
        McpToolSurface.ToolTypes(types).SelectMany(McpToolSurface.ToolMethods).Count();

    private static RouteEndpoint McpPost(WebApplicationFactory<Program> host) =>
        Assert.Single(host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText?.TrimEnd('/') == CluckworkMcp.Path
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) == true);
}
