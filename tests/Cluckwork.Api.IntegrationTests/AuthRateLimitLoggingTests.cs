using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Api.RateLimiting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #273 — Auth.RateLimitRejected: a 429 against the login/refresh policies is a
// brute-force/credential-stuffing signal worth its own stable event; a 429
// against the client-errors policy (#217, log-pipeline volume, not a
// credential) deliberately is NOT. Own factory: needs a TIGHT login limit
// (RateLimitingTests' pattern) which would break every other suite sharing the
// base factory's loose "practically unlimited" override.
public sealed class AuthRateLimitLoggingFactory : CluckworkWebApplicationFactory
{
    public const string TrustedProxy = "10.99.0.2";
    public const int LoginLimit = 3;

    public CollectingSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Login:PermitLimit", LoginLimit.ToString());
        // 24h window: the bucket boundary is wall-clock inside the limiter script,
        // and a boundary crossing mid-loop turns the expected 429 into a 401
        // (#840's 2026-09-16 CI specimens).
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "86400");
        builder.UseSetting("RateLimiting:ClientErrors:PermitLimit", LoginLimit.ToString());
        builder.UseSetting("RateLimiting:ClientErrors:WindowSeconds", "86400");
        builder.UseSetting("RateLimiting:TrustedProxies:0", $"{TrustedProxy}/32");
        // #1164 — one permit per OAuth policy, so the second request is the rejection.
        foreach (var policy in new[] { "OAuthAuthorize", "OAuthToken", "OAuthRegister", "OAuthApi" })
        {
            builder.UseSetting($"RateLimiting:{policy}:PermitLimit", "1");
            builder.UseSetting($"RateLimiting:{policy}:WindowSeconds", "86400");
        }
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
            services.AddSingleton<IStartupFilter, OAuthApiProbe>();
            services.AddSingleton<ILogEventSink>(Sink);
        });
    }

    // No real endpoint accepts OAuth tokens yet; the limiter runs before authentication,
    // so an unauthenticated call still spends the oauth-api permit.
    private sealed class OAuthApiProbe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = (IEndpointRouteBuilder)app.Properties["__EndpointRouteBuilder"]!;
            endpoints.MapGet(OAuthApiProbePath, () => Results.Ok()).AcceptOAuthTokens("cw1164.read");
        };
    }

    public const string OAuthApiProbePath = "/test/oauth/rate-limited";

    public sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

[Collection(AuthRateLimitLoggingCollection.Name)]
public sealed class AuthRateLimitLoggingTests(AuthRateLimitLoggingFactory factory)
{
    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    private IReadOnlyList<LogEvent> EventsFor(string securityEvent) =>
        [.. factory.Sink.Events.Where(e => ScalarOf(e, "SecurityEvent") == securityEvent)];

    private HttpClient ProxiedClient(string clientIp, Uri? baseAddress = null)
    {
        var client = baseAddress is null
            ? factory.CreateClient()
            : factory.CreateClient(new() { BaseAddress = baseAddress, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Remote", AuthRateLimitLoggingFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientIp);
        return client;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode = TestHarness.DefaultFarmCode, email = "nobody@example.com", password = "WrongPassw0rd!" });

    [Fact]
    public async Task Login_rate_limit_rejection_emits_RateLimitRejected_exactly_once()
    {
        factory.Sink.Events.Clear();
        var client = ProxiedClient("203.0.113.201");

        for (var i = 0; i < AuthRateLimitLoggingFactory.LoginLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(client)).StatusCode);
        var limited = await PostLoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var rejected = Assert.Single(EventsFor(SecurityEvents.RateLimitRejected));
        Assert.Equal("203.0.113.201", ScalarOf(rejected, "ClientIp"));
    }

    // #273 codex review (P1c) — the earlier version of this callback matched
    // on a hardcoded list of two literal paths (/auth/login, /auth/refresh),
    // so a rejection against /auth/step-up or /auth/change-password — which
    // AuthEndpoints attaches the SAME LoginPolicyName to, and which
    // deliberately SHARE its budget (a stolen access token must not get
    // unlimited password-guessing attempts on either) — was invisible. This
    // proves the fix: step-up shares the bucket (one permit already spent by
    // the login above), and a rejection there now emits the event, keyed off
    // the endpoint's attached POLICY rather than its path.
    [Fact]
    public async Task StepUp_rate_limit_rejection_emits_RateLimitRejected_because_it_shares_the_login_policy()
    {
        factory.Sink.Events.Clear();
        var clientIp = "203.0.113.203";
        var email = $"steprl-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var loginClient = ProxiedClient(clientIp);
        var loginResponse = await loginClient.PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        loginResponse.EnsureSuccessStatusCode();
        var accessToken = (await loginResponse.Content
            .ReadFromJsonAsync<AccessTokenResponse>())!.AccessToken;

        var stepUpClient = ProxiedClient(clientIp);
        stepUpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // The login above already spent ONE of LoginLimit permits from the
        // policy bucket shared by this client IP — step-up must draw from the
        // SAME bucket, which is exactly the behavior this fix restores.
        for (var i = 1; i < AuthRateLimitLoggingFactory.LoginLimit; i++)
            await stepUpClient.PostAsJsonAsync("/api/v1/auth/step-up", new { password = "WrongPassw0rd!x" });
        var limited = await stepUpClient.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = "WrongPassw0rd!x" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var rejected = Assert.Single(EventsFor(SecurityEvents.RateLimitRejected));
        Assert.Equal(clientIp, ScalarOf(rejected, "ClientIp"));
        Assert.Contains("step-up", ScalarOf(rejected, "Path"));
    }

    // #1164 — every OAuth policy emits the event with its policy name. Each request
    // carries a secret where that endpoint takes one (query string, form body, bearer);
    // none may reach the event.
    private const string Secret = "cw1164-secret-sentinel";

    [Theory]
    [InlineData(RateLimitingOptions.OAuthAuthorizePolicyName, "203.0.113.211")]
    [InlineData(RateLimitingOptions.OAuthTokenPolicyName, "203.0.113.212")]
    [InlineData(RateLimitingOptions.OAuthRegisterPolicyName, "203.0.113.213")]
    [InlineData(RateLimitingOptions.OAuthApiPolicyName, "203.0.113.214")]
    public async Task OAuth_rate_limit_rejection_emits_RateLimitRejected_with_policy_ip_and_path(
        string policy, string clientIp)
    {
        factory.Sink.Events.Clear();
        var client = ProxiedClient(clientIp, new Uri("https://localhost"));
        // Unique per run: oauth-api is keyed per bearer, and the factory outlives one test.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"{Secret}-{Guid.NewGuid():N}");
        var (path, send) = OAuthRequest(policy, client);

        using var allowed = await send();
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        Assert.Empty(EventsFor(SecurityEvents.RateLimitRejected));

        using var limited = await send();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        var rejected = Assert.Single(EventsFor(SecurityEvents.RateLimitRejected));
        Assert.Equal(LogEventLevel.Warning, rejected.Level);
        Assert.Equal(policy, ScalarOf(rejected, "Policy"));
        Assert.Equal(clientIp, ScalarOf(rejected, "ClientIp"));
        Assert.Equal(path, ScalarOf(rejected, "Path"));
        Assert.DoesNotContain(Secret, rejected.RenderMessage());
    }

    private static (string Path, Func<Task<HttpResponseMessage>> Send) OAuthRequest(string policy, HttpClient client) =>
        policy switch
        {
            RateLimitingOptions.OAuthAuthorizePolicyName => ("/api/v1/oauth/authorize",
                () => client.GetAsync($"/api/v1/oauth/authorize?client_id=cw1164&code_challenge={Secret}")),
            RateLimitingOptions.OAuthTokenPolicyName => ("/api/v1/oauth/token",
                () => client.PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = "cw1164",
                    ["code"] = Secret,
                    ["code_verifier"] = Secret,
                }))),
            RateLimitingOptions.OAuthRegisterPolicyName => ("/api/v1/oauth/register",
                () => client.PostAsJsonAsync("/api/v1/oauth/register", new { client_name = Secret })),
            RateLimitingOptions.OAuthApiPolicyName => (AuthRateLimitLoggingFactory.OAuthApiProbePath,
                () => client.GetAsync(AuthRateLimitLoggingFactory.OAuthApiProbePath)),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
        };

    // Scope guard — proves the event is NOT over-fired for the non-auth policy
    // sharing the same OnRejected delegate.
    [Fact]
    public async Task ClientErrors_rate_limit_rejection_does_not_emit_the_auth_security_event()
    {
        factory.Sink.Events.Clear();
        var client = ProxiedClient("203.0.113.202");

        for (var i = 0; i <= AuthRateLimitLoggingFactory.LoginLimit; i++)
        {
            var response = await client.PostAsync("/api/v1/client-errors",
                JsonContent.Create(new { message = "boom" }));
            if (i == AuthRateLimitLoggingFactory.LoginLimit)
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        Assert.Empty(EventsFor(SecurityEvents.RateLimitRejected));
    }
}

[CollectionDefinition(Name)]
public sealed class AuthRateLimitLoggingCollection : ICollectionFixture<AuthRateLimitLoggingFactory>
{
    public const string Name = "auth-rate-limit-logging";
}
