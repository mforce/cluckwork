namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

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
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "900");
        builder.UseSetting("RateLimiting:ClientErrors:PermitLimit", LoginLimit.ToString());
        builder.UseSetting("RateLimiting:ClientErrors:WindowSeconds", "900");
        builder.UseSetting("RateLimiting:TrustedProxies:0", $"{TrustedProxy}/32");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
            services.AddSingleton<ILogEventSink>(Sink);
        });
    }

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

    private HttpClient ProxiedClient(string clientIp)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote", AuthRateLimitLoggingFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientIp);
        return client;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "nobody@example.com", password = "WrongPassw0rd!" });

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
