using System.Net;
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
using ModelContextProtocol.AspNetCore;
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

    private static RouteEndpoint McpPost(WebApplicationFactory<Program> host) =>
        Assert.Single(host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText?.TrimEnd('/') == CluckworkMcp.Path
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) == true);
}
