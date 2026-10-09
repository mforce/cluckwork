using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Insights.Audit;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #800 — an audit row names the connected app a person acted through, beside the person.
[Collection(IntegrationCollection.Name)]
public sealed class AuditConnectedAppTests(CluckworkWebApplicationFactory factory)
{
    private const string WriteScope = "cw800.write";
    private static readonly DateTimeOffset Base = new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed record Created(Guid Id);

    private sealed record AuditRow(
        Guid Id, Guid EntityId, string ActorEmail, string? ConnectedAppClientId, string? ConnectedAppName);

    [Fact]
    public async Task OAuthToken_CarriesTheAppsRegisteredName()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, ClaimsProbe>()));
        var clientId = await RegisterAsync(host, "Field‮Assistant");

        var token = await new OAuthServerTests(factory).ConnectAsync(host, clientId);

        using var probe = OAuthServerTests.HttpsClient(host, token);
        var claims = await probe.GetFromJsonAsync<Dictionary<string, string?>>(ClaimsProbe.Path);
        Assert.Equal(clientId, claims![Claims.ClientId]);
        // #797 turned the bidi override into a space at registration.
        Assert.Equal("Field Assistant", claims[OAuthEndpoints.ClientNameClaim]);
    }

    [Fact]
    public async Task WriteThroughAConnectedApp_RecordsTheAppBesideThePerson()
    {
        using var host = WithAuditProbe();
        var (_, email, jwt) = await OwnerAsync();
        var clientId = await RegisterScopedClientAsync(host, "Field Assistant");
        var token = await ConnectAsync(host, jwt, clientId);

        var entityId = await WriteThroughProbeAsync(host, token);

        var row = await AuditRowAsync(jwt, entityId);
        Assert.Equal(email, row.ActorEmail);
        Assert.Equal(clientId, row.ConnectedAppClientId);
        Assert.Equal("Field Assistant", row.ConnectedAppName);
    }

    [Fact]
    public async Task SessionWrite_RecordsNoApp()
    {
        var (_, email, jwt) = await OwnerAsync();
        using var client = factory.CreateAuthedClient(jwt);

        using var response = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}", phone = "555-0100" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var row = await AuditRowAsync(jwt, (await response.Content.ReadFromJsonAsync<Created>())!.Id);
        Assert.Equal(email, row.ActorEmail);
        Assert.Null(row.ConnectedAppClientId);
        Assert.Null(row.ConnectedAppName);
    }

    [Fact]
    public async Task AppName_SurvivesDisconnectAndPrune()
    {
        using var host = WithAuditProbe();
        var (_, _, jwt) = await OwnerAsync();
        var clientId = await RegisterScopedClientAsync(host, "Barn Helper");
        var entityId = await WriteThroughProbeAsync(host, await ConnectAsync(host, jwt, clientId));

        // Disconnect revokes the authorization (#796); #797's prune later deletes the app.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var applicationId = (await applications.GetIdAsync((await applications.FindByClientIdAsync(clientId))!))!;
            await foreach (var authorization in authorizations.FindByApplicationIdAsync(applicationId))
                Assert.True(await authorizations.TryRevokeAsync(authorization), "the authorization was not revoked");
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await applications.DeleteAsync((await applications.FindByClientIdAsync(clientId))!);
            Assert.Null(await applications.FindByClientIdAsync(clientId));
        }

        var row = await AuditRowAsync(jwt, entityId);
        Assert.Equal(clientId, row.ConnectedAppClientId);
        Assert.Equal("Barn Helper", row.ConnectedAppName);
    }

    [Fact]
    public async Task List_FiltersToConnectedApps_AndToOneApp()
    {
        var accountId = await SeedAccountAsync();
        var person = Event(accountId, app: null, minutes: 0);
        var first = Event(accountId, new ConnectedApp("client-a", "App A"), minutes: 1);
        var second = Event(accountId, new ConnectedApp("client-b", "App B"), minutes: 2);
        await SeedEventsAsync(accountId, person, first, second);

        Assert.Equal([second.Id, first.Id, person.Id], await ListAsync(accountId, false, null));
        Assert.Equal([second.Id, first.Id], await ListAsync(accountId, true, null));
        Assert.Equal([first.Id], await ListAsync(accountId, false, "client-a"));
        Assert.Equal([first.Id], await ListAsync(accountId, true, "client-a"));
    }

    // The repository tests above skip HTTP binding and the Insights contract.
    [Fact]
    public async Task AuditEndpoint_BindsBothConnectedAppFilters()
    {
        var (accountId, _, jwt) = await OwnerAsync();
        var person = Event(accountId, app: null, minutes: 0);
        var first = Event(accountId, new ConnectedApp("client-a", "App A"), minutes: 1);
        var second = Event(accountId, new ConnectedApp("client-b", "App B"), minutes: 2);
        await SeedEventsAsync(accountId, person, first, second);
        using var client = factory.CreateAuthedClient(jwt);

        async Task<Guid[]> IdsAsync(string query) =>
            (await client.GetFromJsonAsync<List<AuditRow>>($"/api/v1/audit?{query}"))!.Select(row => row.Id).ToArray();

        Assert.Equal([second.Id, first.Id], await IdsAsync("connectedAppsOnly=true"));
        Assert.Equal([first.Id], await IdsAsync("connectedAppClientId=client-a"));
        Assert.Equal([second.Id, first.Id, person.Id], await IdsAsync("limit=50"));
    }

    [Fact]
    public async Task List_AppFilter_StaysInsideTheFarm()
    {
        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var app = new ConnectedApp("client-shared", "Shared App");
        var own = Event(mine, app, minutes: 0);
        await SeedEventsAsync(mine, own);
        await SeedEventsAsync(theirs, Event(theirs, app, minutes: 1));

        Assert.Equal([own.Id], await ListAsync(mine, true, null));
        Assert.Equal([own.Id], await ListAsync(mine, false, "client-shared"));
    }

    // #508 — the filtered list keeps the Sequence tiebreak. The row written second has
    // the lower id, so an Id tiebreak would put it last.
    [Fact]
    public async Task List_AppFilter_KeepsWriteOrderForTiedInstants()
    {
        var accountId = await SeedAccountAsync();
        var app = new ConnectedApp("client-tie", "Tie App");
        var earlier = AuditEvent.Create(
            new Guid("ffffffff-ffff-4fff-bfff-ffffffff0800"), accountId, Base, Guid.NewGuid(),
            "earlier@farm.test", "Customer.Update", "Customer", Guid.NewGuid(), connectedApp: app);
        var later = AuditEvent.Create(
            new Guid("00000000-0000-4000-8000-000000000800"), accountId, Base, Guid.NewGuid(),
            "later@farm.test", "Customer.Update", "Customer", Guid.NewGuid(), connectedApp: app);
        await SeedEventsAsync(accountId, earlier);
        await SeedEventsAsync(accountId, later);

        Assert.Equal([later.Id, earlier.Id], await ListAsync(accountId, true, null));
    }

    [Fact]
    public async Task Export_IncludesTheAppColumns()
    {
        var (accountId, _, jwt) = await OwnerAsync();
        await SeedEventsAsync(accountId, Event(accountId, new ConnectedApp("client-csv", "Csv App"), minutes: 0));

        using var client = factory.CreateAuthedClient(jwt);
        var lines = (await client.GetStringAsync("/api/v1/export/audit-events")).TrimStart('﻿').Split("\r\n");

        Assert.Equal(
            "id,occurredAtUtc,actorUserId,actorEmail,action,entityType,entityId,reason,detailsJson,connectedAppClientId,connectedAppName",
            lines[0]);
        Assert.Contains(lines, line => line.EndsWith(",client-csv,Csv App"));
    }

    private WebApplicationFactory<Program> WithAuditProbe() =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, AuditProbe>();
            services.Configure<OpenIddictServerOptions>(options => options.Scopes.Add(WriteScope));
        }));

    private async Task<(Guid AccountId, string Email, string Jwt)> OwnerAsync()
    {
        var email = $"app-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        return (accountId, email, await factory.LoginForAccessTokenAsync(email));
    }

    private Task<Guid> SeedAccountAsync() =>
        factory.SeedAccountWithUserAsync($"app-{Guid.NewGuid():N}@test.local");

    private static async Task<Guid> WriteThroughProbeAsync(WebApplicationFactory<Program> host, string token)
    {
        using var client = OAuthServerTests.HttpsClient(host, token);
        using var response = await client.PostWithKeyAsync(AuditProbe.Path, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private static async Task<string> RegisterScopedClientAsync(WebApplicationFactory<Program> host, string name)
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(
            new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientType = ClientTypes.Public,
                DisplayName = name,
                RedirectUris = { new Uri(OAuthServerTests.RedirectUri) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.ResponseTypes.Code,
                    Permissions.Prefixes.Scope + WriteScope,
                },
            });
        return clientId;
    }

    // A user approves clientId for WriteScope and the client redeems its code.
    private static async Task<string> ConnectAsync(WebApplicationFactory<Program> host, string jwt, string clientId)
    {
        var verifier = OAuthServerTests.NewCodeVerifier();
        var query = OAuthServerTests.AuthorizeQuery(clientId, verifier);
        query["scope"] = WriteScope;
        using var authorize = await OAuthServerTests.SendAuthorizeAsync(host, jwt, query);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();

        using var client = OAuthServerTests.HttpsClient(host, bearer: null);
        using var token = await client.PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = OAuthServerTests.RedirectUri,
                ["code_verifier"] = verifier,
            }));
        token.EnsureSuccessStatusCode();
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private async Task<AuditRow> AuditRowAsync(string jwt, Guid entityId)
    {
        using var client = factory.CreateAuthedClient(jwt);
        return Assert.Single((await client.GetFromJsonAsync<List<AuditRow>>($"/api/v1/audit?entityId={entityId}"))!);
    }

    private static async Task<string> RegisterAsync(WebApplicationFactory<Program> host, string name)
    {
        using var client = OAuthServerTests.HttpsClient(host, bearer: null);
        using var response = await client.PostAsJsonAsync("/api/v1/oauth/register", new Dictionary<string, object>
        {
            ["client_name"] = name,
            ["redirect_uris"] = new[] { OAuthServerTests.RedirectUri },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static AuditEvent Event(Guid accountId, ConnectedApp? app, int minutes) =>
        AuditEvent.Create(Guid.NewGuid(), accountId, Base.AddMinutes(minutes), Guid.NewGuid(),
            "someone@farm.test", "Customer.Update", "Customer", Guid.NewGuid(), connectedApp: app);

    private Task SeedEventsAsync(Guid accountId, params AuditEvent[] events) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.AuditEvents.AddRange(events);
            await db.SaveChangesAsync();
        });

    private async Task<Guid[]> ListAsync(Guid accountId, bool connectedAppsOnly, string? clientId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var rows = await scope.ServiceProvider.GetRequiredService<IAuditEventRepository>()
            .ListAsync(null, null, null, null, null, connectedAppsOnly, clientId, 50, 0);
        return rows.Select(row => row.Id).ToArray();
    }

    // Returns the validated OAuth principal's client claims; exists only in this test host.
    private sealed class ClaimsProbe : IStartupFilter
    {
        public const string Path = "/test/oauth-claims";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(Path, probe =>
            {
                probe.UseAuthentication();
                probe.Run(async context =>
                {
                    var result = await context.AuthenticateAsync(
                        OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                    if (!result.Succeeded)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    await context.Response.WriteAsJsonAsync(new Dictionary<string, string?>
                    {
                        [Claims.ClientId] = result.Principal!.FindFirst(Claims.ClientId)?.Value,
                        [OAuthEndpoints.ClientNameClaim] = result.Principal.FindFirst(OAuthEndpoints.ClientNameClaim)?.Value,
                    });
                });
            });
            next(app);
        };
    }

    // A write endpoint that accepts OAuth tokens, as #806's tools will. Mapped after
    // Program.cs builds its endpoint table, behind every middleware a business endpoint
    // has (#796's pattern), and writing through the real IAuditWriter.
    private sealed class AuditProbe : IStartupFilter
    {
        public const string Path = "/test/oauth/audit-write";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = (IEndpointRouteBuilder)app.Properties["__EndpointRouteBuilder"]!;
            endpoints.MapPost(Path, async (IAuditWriter audit, AppDbContext db) =>
                {
                    var id = Guid.NewGuid();
                    await audit.WriteAsync("Customer.Update", "Customer", id);
                    await db.SaveChangesAsync();
                    return Results.Ok(new { id });
                })
                .AcceptOAuthTokens(WriteScope)
                .RequireAuthorization();
        };
    }
}
