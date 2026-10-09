using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Infrastructure.RateLimiting;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #797 — RFC 7591 registration: who may register, with what, and how often.
[Collection(IntegrationCollection.Name)]
public sealed class OAuthClientRegistrationTests(CluckworkWebApplicationFactory factory)
{
    private const string RegisterPath = "/api/v1/oauth/register";

    [Fact]
    public async Task SelfRegisteredClient_CompletesTheFlow()
    {
        using var response = await RegisterAsync(factory, new
        {
            client_name = "Test Assistant",
            redirect_uris = new[] { OAuthServerTests.RedirectUri, "http://127.0.0.1:53682/callback" },
            grant_types = new[] { GrantTypes.AuthorizationCode, GrantTypes.RefreshToken },
            response_types = new[] { ResponseTypes.Code },
            token_endpoint_auth_method = ClientAuthenticationMethods.None,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = body.GetProperty("client_id").GetString()!;
        Assert.Equal("Test Assistant", body.GetProperty("client_name").GetString());
        // refresh_token was asked for and not granted: tokens last until revoked (#788).
        Assert.Equal([GrantTypes.AuthorizationCode], Strings(body, "grant_types"));
        Assert.Equal(ClientAuthenticationMethods.None, body.GetProperty("token_endpoint_auth_method").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _), "a public client was given a secret");

        await using var scope = factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await applications.FindByClientIdAsync(clientId))!;
        Assert.Equal(ClientTypes.Public, await applications.GetClientTypeAsync(application));
        Assert.Equal(
            [Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
             Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code],
            (await applications.GetPermissionsAsync(application)).Order());

        Assert.NotEmpty(await new OAuthServerTests(factory).ConnectAsync(factory, clientId));
    }

    [Fact]
    public async Task Discovery_AdvertisesTheRegistrationEndpoint()
    {
        using var response = await OAuthServerTests.HttpsClient(factory, bearer: null)
            .GetAsync("/.well-known/oauth-authorization-server");

        var metadata = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(metadata.TryGetProperty("registration_endpoint", out var endpoint),
            "discovery does not advertise registration_endpoint");
        Assert.Equal("https://localhost/api/v1/oauth/register", endpoint.GetString());
    }

    // Registration is anonymous whatever the caller sends: a session bearer must not
    // resolve a farm and pull the request into the idempotency protocol.
    [Fact]
    public async Task Registration_IgnoresASessionBearer()
    {
        var email = $"oauth-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var jwt = await factory.LoginForAccessTokenAsync(email);

        using var response = await OAuthServerTests.HttpsClient(factory, jwt).PostAsJsonAsync(
            RegisterPath, new { redirect_uris = new[] { OAuthServerTests.RedirectUri } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:53682/callback")]
    [InlineData("http://[::1]:8080/cb")]
    [InlineData("http://localhost:3000/callback")]
    [InlineData("https://client.example/cb?x=1")]
    public async Task AllowedRedirectUri_IsRegistered(string redirect)
    {
        using var response = await RegisterAsync(factory, new { redirect_uris = new[] { redirect } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("http://client.example/cb")]
    [InlineData("http://127.0.0.2/cb")]
    [InlineData("com.example.app:/cb")]
    [InlineData("cursor://anysphere.cursor-retrieval/oauth/callback")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://client.example/cb#fragment")]
    [InlineData("https://user@client.example/cb")]
    [InlineData("/relative/cb")]
    public async Task WiderRedirectUri_IsRefused(string redirect)
    {
        using var response = await RegisterAsync(factory, new { redirect_uris = new[] { OAuthServerTests.RedirectUri, redirect } });

        await AssertErrorAsync(response, "invalid_redirect_uri");
    }

    // OpenIddict refuses an iss parameter in a callback (issuer fixation); that must come
    // back as a registration error, not a 500.
    [Fact]
    public async Task ReservedRedirectParameter_IsARegistrationError()
    {
        using var response = await RegisterAsync(factory, new { redirect_uris = new[] { "https://client.example/cb?iss=x" } });

        await AssertErrorAsync(response, "invalid_redirect_uri");
    }

    // RFC 8252 §7.3: a native client registers once and listens on whatever port it gets.
    [Theory]
    [InlineData("http://127.0.0.1:5000/cb", "http://127.0.0.1:54321/cb")]
    [InlineData("http://[::1]:5000/cb", "http://[::1]:54321/cb")]
    [InlineData("http://localhost:5000/cb", "http://localhost:54321/cb")]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:49152/cb")]
    [InlineData("http://localhost:5000/cb?tab=1", "http://localhost:54321/cb?tab=1")]
    public async Task LoopbackClient_AuthorizesOnAnotherPort(string registered, string authorized)
    {
        var clientId = await RegisteredClientIdAsync(registered);

        Assert.NotEmpty(await new OAuthServerTests(factory).ConnectAsync(factory, clientId, authorized));
    }

    // Only the port varies; OpenIddict still compares everything else.
    [Theory]
    [InlineData("http://127.0.0.1:5000/cb", "http://127.0.0.1:54321/other")]
    [InlineData("http://127.0.0.1:5000/cb", "http://localhost:54321/cb")]
    [InlineData("http://127.0.0.1:5000/cb?tab=1", "http://127.0.0.1:54321/cb?tab=2")]
    [InlineData("http://127.0.0.1:5000/cb", "https://127.0.0.1:54321/cb")]
    public async Task LoopbackClient_OnAnotherPort_StillMatchesTheRest(string registered, string authorized)
    {
        var clientId = await RegisteredClientIdAsync(registered);
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt,
            OAuthServerTests.AuthorizeQuery(clientId, OAuthServerTests.NewCodeVerifier(), authorized));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("redirect_uri", await response.Content.ReadAsStringAsync());
    }

    // The response lists what OpenIddict stored and compares ordinally, so a client can
    // use those strings as they are.
    [Theory]
    [InlineData("https://client.example")]
    [InlineData("HTTPS://Client.Example:443/cb")]
    [InlineData("http://localhost:5000/cb")]
    public async Task ReturnedRedirectUri_IsUsable(string registered)
    {
        using var response = await RegisterAsync(factory, new { redirect_uris = new[] { registered } });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var returned = Assert.Single(Strings(body, "redirect_uris"));

        Assert.NotEmpty(await new OAuthServerTests(factory).ConnectAsync(
            factory, body.GetProperty("client_id").GetString()!, returned));
    }

    [Fact]
    public async Task MissingRedirectUris_AreRefused()
    {
        using var missing = await RegisterAsync(factory, new { client_name = "No Redirects" });
        using var empty = await RegisterAsync(factory, new { redirect_uris = Array.Empty<string>() });

        await AssertErrorAsync(missing, "invalid_redirect_uri");
        await AssertErrorAsync(empty, "invalid_redirect_uri");
    }

    public static TheoryData<string, string[]?, string[]?, string?> WiderGrants => new()
    {
        { "implicit", [GrantTypes.Implicit], null, null },
        { "client credentials", [GrantTypes.ClientCredentials], null, null },
        { "password beside code", [GrantTypes.AuthorizationCode, GrantTypes.Password], null, null },
        { "refresh token alone", [GrantTypes.RefreshToken], null, null },
        { "token response", null, [ResponseTypes.Token], null },
        { "client secret", null, null, ClientAuthenticationMethods.ClientSecretBasic },
    };

    [Theory]
    [MemberData(nameof(WiderGrants))]
    public async Task WiderGrant_IsRefused(string _, string[]? grants, string[]? responses, string? authMethod)
    {
        using var response = await RegisterAsync(factory, new
        {
            redirect_uris = new[] { OAuthServerTests.RedirectUri },
            grant_types = grants,
            response_types = responses,
            token_endpoint_auth_method = authMethod,
        });

        await AssertErrorAsync(response, "invalid_client_metadata");
    }

    public static TheoryData<string, string?> Names => new()
    {
        { "Claude‮gnp.exe", "Claude gnp.exe" },
        { "⁦Farm⁩ Bank", "Farm Bank" },
        { "  My\n\tApp\u0000 ", "My App" },
        { "zero​width", "zero width" },
        { "Café", "Café" },
        { new string('x', 150), new string('x', 100) },
        { "e" + new string('́', 300), null },
        { "‮\u0000", null },
    };

    // The consent screen (#798) shows this name, so what is stored is what it shows.
    [Theory]
    [MemberData(nameof(Names))]
    public async Task ClientName_IsSanitized(string name, string? expected)
    {
        using var response = await RegisterAsync(factory, new
        {
            client_name = name,
            redirect_uris = new[] { OAuthServerTests.RedirectUri },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, body.GetProperty("client_name").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applications.FindByClientIdAsync(body.GetProperty("client_id").GetString()!);
        Assert.Equal(expected, await applications.GetDisplayNameAsync(application!));
    }

    // Per client IP behind a trusted proxy (#260), on the shared counter (#543), so N
    // replicas enforce one budget.
    [Fact]
    public async Task Registration_IsRateLimitedPerClientIp_OnTheSharedCounter()
    {
        const string proxy = "10.99.0.7";
        var counter = new RecordingCounter();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:OAuthRegister:PermitLimit", "2");
            // Clock-aligned window: a day keeps the requests inside one (see OAuthFailClosedTests.Host).
            builder.UseSetting("RateLimiting:OAuthRegister:WindowSeconds", "86400");
            builder.UseSetting("RateLimiting:TrustedProxies:0", $"{proxy}/32");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
                services.RemoveAll<IFixedWindowCounter>();
                services.AddSingleton<IFixedWindowCounter>(counter);
            });
        });
        var first = $"203.0.113.{Random.Shared.Next(1, 250)}";
        var second = $"198.51.100.{Random.Shared.Next(1, 250)}";

        var statuses = new List<HttpStatusCode>();
        foreach (var ip in new[] { first, first, first, second })
        {
            using var response = await RegisterAsync(host, new { redirect_uris = new[] { OAuthServerTests.RedirectUri } },
                client =>
                {
                    client.DefaultRequestHeaders.Add("X-Test-Remote", proxy);
                    client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
                });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests, HttpStatusCode.Created],
            statuses);
        Assert.True(counter.Keys.Contains(
                $"{RateLimitingOptions.OAuthRegisterPolicyName}:{RateLimitKey.ForClient(IPAddress.Parse(first))}"),
            "the registration limit did not count on the shared counter");
    }

    private sealed class RecordingCounter : IFixedWindowCounter
    {
        private readonly InProcessFixedWindowCounter inner = new(TimeProvider.System);
        public System.Collections.Concurrent.ConcurrentBag<string> Keys { get; } = [];

        public long Increment(string key, TimeSpan window)
        {
            Keys.Add(key);
            return inner.Increment(key, window);
        }

        public ValueTask<FixedWindowResult> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            return inner.IncrementAsync(key, window, cancellationToken);
        }
    }

    private async Task<string> RegisteredClientIdAsync(string redirect)
    {
        using var response = await RegisterAsync(factory, new { redirect_uris = new[] { redirect } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static string[] Strings(JsonElement body, string name) =>
        [.. body.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];

    private static async Task<HttpResponseMessage> RegisterAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, object body,
        Action<HttpClient>? configure = null)
    {
        var client = OAuthServerTests.HttpsClient(host, bearer: null);
        configure?.Invoke(client);
        return await client.PostAsJsonAsync(RegisterPath, body);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(error, body.GetProperty("error").GetString());
    }
}
