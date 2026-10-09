using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Serilog.Core;
using Serilog.Events;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #795 — the authorization-code + PKCE flow end to end, and the wall that keeps its
// token away from business endpoints. OAuthFailClosedTests covers the endpoints that
// accept it (#796).
[Collection(IntegrationCollection.Name)]
public sealed class OAuthServerTests(CluckworkWebApplicationFactory factory)
{
    internal const string RedirectUri = "https://client.example/callback";

    [Fact]
    public async Task AuthorizationCodeWithPkce_IssuesAReferenceTokenTheResourceSideAccepts()
    {
        using var host = WithResourceProbe(factory);
        var (userId, jwt) = await SeedUserAsync();
        var clientId = await RegisterClientAsync(host.Services);
        var verifier = NewCodeVerifier();

        var code = await AuthorizeAsync(host, jwt, clientId, verifier);
        using var tokenResponse = await RedeemAsync(host, clientId, code, verifier);
        tokenResponse.EnsureSuccessStatusCode();
        var body = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("access_token").GetString()!;

        // Indefinite until revoked, and nothing beside the access token (#788).
        Assert.False(body.TryGetProperty("expires_in", out _), "the access token carries expires_in");
        Assert.False(body.TryGetProperty("refresh_token", out _), "the response carries a refresh_token");
        Assert.False(body.TryGetProperty("id_token", out _), "the response carries an id_token");
        // A reference token: the caller holds an id, the payload lives in the table.
        await using var db = factory.Services.CreateAsyncScope().ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken<Guid>>()
            .Where(token => token.Subject == userId.ToString() && token.Type == TokenTypeIdentifiers.AccessToken)
            .SingleAsync();
        Assert.True(stored.ReferenceId is not null, "the access token has no ReferenceId");
        Assert.True(stored.ExpirationDate is null, "the stored access token expires");
        Assert.Equal(userId.ToString(), await ProbeAsync(host, accessToken));
    }

    [Fact]
    public async Task AuthorizationRequestWithoutPkce_IsRefused()
    {
        using var host = WithResourceProbe(factory);
        var (_, jwt) = await SeedUserAsync();
        var clientId = await RegisterClientAsync(host.Services);

        using var response = await SendAuthorizeAsync(host, jwt, new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = ResponseTypes.Code,
        });

        // Refused before the redirect URI is trusted, so OpenIddict answers directly.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"error:{Errors.InvalidRequest}", body);
        Assert.Contains("code_challenge", body);
    }

    [Fact]
    public async Task PlainCodeChallenge_IsRefused()
    {
        using var host = WithResourceProbe(factory);
        var (_, jwt) = await SeedUserAsync();
        var clientId = await RegisterClientAsync(host.Services);
        var verifier = NewCodeVerifier();
        var query = AuthorizeQuery(clientId, verifier);
        query["code_challenge"] = verifier;
        query["code_challenge_method"] = CodeChallengeMethods.Plain;

        using var response = await SendAuthorizeAsync(host, jwt, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"error:{Errors.InvalidRequest}", await response.Content.ReadAsStringAsync());
    }

    // Granted, openid would mint an identity token signed with a per-process key.
    [Fact]
    public async Task OpenIdScope_IsRefused()
    {
        using var host = WithResourceProbe(factory);
        var (_, jwt) = await SeedUserAsync();
        var clientId = await RegisterClientAsync(host.Services);
        var query = AuthorizeQuery(clientId, NewCodeVerifier());
        query["scope"] = Scopes.OpenId;

        using var response = await SendAuthorizeAsync(host, jwt, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"error:{Errors.InvalidScope}", await response.Content.ReadAsStringAsync());
    }

    // #798 — a browser navigation carries no session bearer, so the SPA's consent route
    // takes the validated request over, unchanged.
    [Fact]
    public async Task AuthorizationRequest_WithoutASignedInUser_GoesToTheConsentRoute()
    {
        using var host = WithResourceProbe(factory);
        var clientId = await RegisterClientAsync(host.Services);
        var query = AuthorizeQuery(clientId, NewCodeVerifier());

        using var response = await SendAuthorizeAsync(host, jwt: null, query);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(QueryHelpers.AddQueryString("/connect", query), response.Headers.Location!.OriginalString);
    }

    // OpenIddict's keys here are ephemeral, so two hosts never share them. A code one
    // host issues redeems on another, and its token validates on both, only because
    // both read the shared Data Protection ring (#794).
    [Fact]
    public async Task CodeAndToken_CrossReplicas_ThroughTheSharedKeyRing()
    {
        using var first = WithResourceProbe(factory);
        using var second = WithResourceProbe(factory);
        var (userId, jwt) = await SeedUserAsync();
        var clientId = await RegisterClientAsync(first.Services);
        var verifier = NewCodeVerifier();

        var code = await AuthorizeAsync(first, jwt, clientId, verifier);
        using var tokenResponse = await RedeemAsync(second, clientId, code, verifier);
        Assert.True(tokenResponse.IsSuccessStatusCode,
            $"the second replica refused the first replica's code with {(int)tokenResponse.StatusCode}");
        var accessToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;

        Assert.Equal(userId.ToString(), await ProbeAsync(first, accessToken));
        Assert.Equal(userId.ToString(), await ProbeAsync(second, accessToken));
    }

    // Business endpoints authenticate with the session JWT scheme only (#796 routes the
    // OAuth handler to opted-in endpoints), so the OAuth token fails authentication
    // before any middleware reads a claim.
    [Fact]
    public async Task OAuthToken_IsRejectedByBusinessEndpoints_AtAuthentication()
    {
        using var host = WithResourceProbe(factory);
        var accessToken = await IssueAccessTokenAsync(host);

        using var response = await HttpsClient(factory, accessToken).GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header =>
            header.Scheme == "Bearer" && header.Parameter?.Contains("invalid_token") == true);
    }

    // Configuration asks for every OpenIddict event; still no secret from a redeemed
    // or a refused exchange may reach a sink.
    [Fact]
    public async Task ProtocolSecrets_NeverReachTheLog()
    {
        var sink = new CollectingSink();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            builder.UseSetting("Serilog:MinimumLevel:Override:OpenIddict", "Verbose");
            // The most specific override wins in Serilog, so a parent clamp alone would lose.
            builder.UseSetting("Serilog:MinimumLevel:Override:OpenIddict.Server", "Verbose");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, ResourceProbe>();
                services.AddSingleton<ILogEventSink>(sink);
            });
        });
        var (_, jwt) = await SeedUserAsync();
        var clientId = await RegisterClientAsync(host.Services);
        var verifier = $"cw795-verifier-marker-{Guid.NewGuid():N}";
        var refusedVerifier = $"cw795-verifier-marker-{Guid.NewGuid():N}";
        var refusedCode = $"cw795-code-marker-{Guid.NewGuid():N}";

        var code = await AuthorizeAsync(host, jwt, clientId, verifier);
        using var redeemed = await RedeemAsync(host, clientId, code, verifier);
        redeemed.EnsureSuccessStatusCode();
        var accessToken = (await redeemed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
        using var refused = await RedeemAsync(host, clientId, refusedCode, refusedVerifier);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        string[] secrets = [verifier, refusedVerifier, refusedCode, code, accessToken];
        var logged = sink.Events.Select(RenderAll).ToList();
        Assert.True(logged.Any(text => text.Contains("/api/v1/oauth/token")),
            "the log tap saw no token request");
        var leaks = logged.Count(text => secrets.Any(text.Contains));
        Assert.True(leaks == 0, $"protocol secrets reached the log in {leaks} event(s)");
    }

    private static string RenderAll(LogEvent logEvent) => string.Join('\n',
        [logEvent.RenderMessage(), .. logEvent.Properties.Values.Select(value => value.ToString()),
         logEvent.Exception?.ToString() ?? ""]);

    private sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private async Task<string> IssueAccessTokenAsync(WebApplicationFactory<Program> host) =>
        await ConnectAsync(host, await RegisterClientAsync(host.Services));

    // A user approves clientId and the client redeems its code: one live connection.
    internal async Task<string> ConnectAsync(
        WebApplicationFactory<Program> host, string clientId, string redirectUri = RedirectUri)
    {
        var (_, jwt) = await SeedUserAsync();
        var verifier = NewCodeVerifier();
        var code = await AuthorizeAsync(host, jwt, clientId, verifier, redirectUri);
        using var response = await RedeemAsync(host, clientId, code, verifier, redirectUri);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    internal async Task<(Guid UserId, string Jwt)> SeedUserAsync()
    {
        var email = $"oauth-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync());
        return (userId, await factory.LoginForAccessTokenAsync(email));
    }

    internal static async Task<string> RegisterClientAsync(IServiceProvider services)
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(
            new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientType = ClientTypes.Public,
                RedirectUris = { new Uri(RedirectUri) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.ResponseTypes.Code,
                },
            });
        return clientId;
    }

    private static async Task<string> AuthorizeAsync(
        WebApplicationFactory<Program> host, string jwt, string clientId, string verifier,
        string redirectUri = RedirectUri)
    {
        var location = await ApproveAsync(host, jwt, AuthorizeQuery(clientId, verifier, redirectUri));
        Assert.StartsWith(redirectUri.Split('?')[0], location.OriginalString);
        return QueryHelpers.ParseQuery(location.Query)["code"].ToString();
    }

    // The consent route's Allow (#798): the user re-enters their password for a step-up
    // grant, and the approval comes back as the client's redirect.
    internal static async Task<Uri> ApproveAsync(
        WebApplicationFactory<Program> host, string jwt, Dictionary<string, string?> query)
    {
        using var response = await SendAuthorizeAsync(host, jwt, query, await StepUpAsync(host, jwt));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return RedirectOf(await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    internal static Uri RedirectOf(JsonElement body) => new(body.GetProperty("redirectUri").GetString()!);

    internal static async Task<string> StepUpAsync(WebApplicationFactory<Program> host, string jwt)
    {
        using var response = await HttpsClient(host, jwt).PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    internal static Dictionary<string, string?> AuthorizeQuery(
        string clientId, string verifier, string redirectUri = RedirectUri) => new()
    {
        ["client_id"] = clientId,
        ["redirect_uri"] = redirectUri,
        ["response_type"] = ResponseTypes.Code,
        ["code_challenge"] = Base64UrlEncoder(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
        ["code_challenge_method"] = CodeChallengeMethods.Sha256,
        ["state"] = "state-795",
    };

    internal static Task<HttpResponseMessage> SendAuthorizeAsync(
        WebApplicationFactory<Program> host, string? jwt, Dictionary<string, string?> query,
        string? stepUp = null, string? consent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString("/api/v1/oauth/authorize", query));
        if (stepUp is not null) request.Headers.Add(AuthEndpoints.StepUpHeaderName, stepUp);
        if (consent is not null) request.Headers.Add(OAuthEndpoints.ConsentHeaderName, consent);
        return HttpsClient(host, jwt).SendAsync(request);
    }

    private static Task<HttpResponseMessage> RedeemAsync(
        WebApplicationFactory<Program> host, string clientId, string code, string verifier,
        string redirectUri = RedirectUri) =>
        HttpsClient(host, bearer: null).PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }));

    private static async Task<string?> ProbeAsync(WebApplicationFactory<Program> host, string accessToken)
    {
        using var response = await HttpsClient(host, accessToken).GetAsync(ResourceProbe.Path);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
    }

    internal static HttpClient HttpsClient(WebApplicationFactory<Program> host, string? bearer)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    internal static string NewCodeVerifier() => Base64UrlEncoder(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncoder(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static WebApplicationFactory<Program> WithResourceProbe(CluckworkWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, ResourceProbe>()));

    // A resource endpoint that accepts OAuth tokens, as #796 will make the business
    // endpoints do. It exists only in this test host.
    private sealed class ResourceProbe : IStartupFilter
    {
        public const string Path = "/test/oauth-resource";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(Path, probe =>
            {
                // OpenIddict validation reads state its request handler stores in UseAuthentication.
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

                    await context.Response.WriteAsync(result.Principal!.FindFirst(Claims.Subject)!.Value);
                });
            });
            next(app);
        };
    }
}
