using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #805 — the bridge resolves for a real OAuth caller behind the real middleware, so its
// checks are not refusing every request the way a fresh scope would.
[Collection(IntegrationCollection.Name)]
public sealed class McpCallContextPipelineTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task OAuthCaller_ResolvesTheRequestsIdentity()
    {
        using var host = Host();
        var clientId = await OAuthServerTests.RegisterClientAsync(host.Services);
        var token = await new OAuthServerTests(factory).ConnectAsync(host, clientId);

        using var response = await OAuthServerTests.HttpsClient(host, token).GetAsync(Probe.OAuth);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(clientId, body.GetProperty("clientId").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("userId").GetGuid());
        Assert.Equal(body.GetProperty("tenantAccountId").GetGuid(), body.GetProperty("accountId").GetGuid());
        Assert.Equal(body.GetProperty("currentUserId").GetGuid(), body.GetProperty("userId").GetGuid());
    }

    // The second line behind AcceptOAuthTokens: a session JWT carries no client_id.
    [Fact]
    public async Task SessionJwtCaller_IsRefused()
    {
        using var host = Host();
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();

        using var response = await OAuthServerTests.HttpsClient(host, jwt).GetAsync(Probe.Session);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("connected app", await response.Content.ReadAsStringAsync());
    }

    private WebApplicationFactory<Program> Host() =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, Probe>()));

    // Mapped into Program.cs's endpoint table, behind every middleware a business
    // endpoint has. A refusal is returned as 409 so the test reads its reason.
    private sealed class Probe : IStartupFilter
    {
        public const string OAuth = "/test/mcp/oauth";
        public const string Session = "/test/mcp/session";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = (IEndpointRouteBuilder)app.Properties["__EndpointRouteBuilder"]!;
            endpoints.MapGet(OAuth, Resolve).AcceptOAuthTokens(OAuthScopes.ReadFarm).RequireAuthorization();
            endpoints.MapGet(Session, Resolve).RequireAuthorization();
        };

        private static IResult Resolve(HttpContext context)
        {
            try
            {
                var call = context.RequestServices.GetRequiredService<McpCallContext>();
                return Results.Ok(new
                {
                    call.AccountId,
                    call.UserId,
                    call.ConnectedApp.ClientId,
                    TenantAccountId = context.RequestServices.GetRequiredService<TenantContext>().AccountId,
                    CurrentUserId = context.RequestServices.GetRequiredService<ICurrentUser>().UserId,
                });
            }
            catch (InvalidOperationException refusal)
            {
                return Results.Conflict(refusal.Message);
            }
        }
    }
}
