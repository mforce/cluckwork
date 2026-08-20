namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.RateLimiting;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Serilog.Events;
using StackExchange.Redis;

// #544 — the WIRING of the distributed limiter, driven through the real
// rate-limiting middleware on a CluckworkWebApplicationFactory with the
// DI-registered IFixedWindowCounter replaced by a controllable double:
// ResilientFixedWindowCounter over a TOGGLEABLE primary stub, so these tests
// exercise the wired limiter's degradation (and recovery) without Docker/Redis
// — the counter itself is unit-tested in SharedState/ResilientFallbackTests.
//
// Proven here:
//   (b) primary down → requests are still served (fallback), a budget is still
//       enforced, and the SecurityEvents.SharedStateRedisUnavailable alarm fires;
//   (c) primary recovered (no host restart) → traffic returns to the shared path;
//   (d) the limit fires BEFORE account lookup (unknown user still 429s) and the
//       counter only ever sees the derived IP key;
//   (e) no attacker-supplied dimension (the email) leaks into a counter key or
//       the RateLimitRejected log event.
public sealed class DistributedRateLimiterWiringFactory : CluckworkWebApplicationFactory
{
    public const string TrustedProxy = "10.99.0.3";
    public const int LoginLimit = 3;

    public CollectingSink Sink { get; } = new();
    // internal, not public: ToggleablePrimary.IncrementAsync returns the internal
    // FixedWindowResult, so a public property/type here would be CS0050 (a public member
    // cannot expose a less-accessible type). Same-assembly test access is unaffected.
    internal ToggleablePrimary Primary { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Login:PermitLimit", LoginLimit.ToString());
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "900");
        builder.UseSetting("RateLimiting:TrustedProxies:0", $"{TrustedProxy}/32");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
            services.AddSingleton<ILogEventSink>(Sink);
            // Replace the registered (resilient, Redis-backed) counter with the
            // controllable double: resilient decorator + toggleable primary, so
            // the WIRED limiter's degradation path is under test, not the raw
            // counter. IFixedWindowCounter is internal to Infrastructure, visible
            // here via InternalsVisibleTo Cluckwork.Api.IntegrationTests.
            services.RemoveAll<IFixedWindowCounter>();
            services.AddSingleton<IFixedWindowCounter>(sp =>
                new ResilientFixedWindowCounter(
                    Primary,
                    new InProcessFixedWindowCounter(TimeProvider.System),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResilientFixedWindowCounter>>()));
        });
    }

    public sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    // The "Redis" stub: throws while _down (exactly the shape
    // ResilientFallbackTests' throwing stubs use), otherwise delegates to its
    // own in-process counter — and RECORDS every key it sees, so tests (d)/(e)
    // can assert on what the shared store was asked to count.
    internal sealed class ToggleablePrimary : IFixedWindowCounter
    {
        private readonly InProcessFixedWindowCounter _inner = new(TimeProvider.System);
        private volatile bool _down = true;

        public List<string> SeenKeys { get; } = [];

        public bool IsDown => _down;
        public void SetDown(bool down) => _down = down;
        public void ClearSeenKeys() { lock (SeenKeys) { SeenKeys.Clear(); } }

        public long Increment(string key, TimeSpan window)
        {
            if (_down)
                throw new RedisException("down");
            lock (SeenKeys) { SeenKeys.Add(key); }
            return _inner.Increment(key, window);
        }

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default)
        {
            if (_down)
                throw new RedisException("down");
            lock (SeenKeys) { SeenKeys.Add(key); }
            return _inner.IncrementAsync(key, window, cancellationToken);
        }
    }
}

[Collection(DistributedRateLimiterWiringCollection.Name)]
public sealed class DistributedRateLimiterWiringTests(DistributedRateLimiterWiringFactory factory)
{
    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    private IReadOnlyList<LogEvent> EventsFor(string securityEvent) =>
        [.. factory.Sink.Events.Where(e => ScalarOf(e, "SecurityEvent") == securityEvent)];

    // The counter key the login policy composes: "auth-login:<ip-key>".
    private static string ExpectedKey(string ip) =>
        $"{RateLimitingOptions.LoginPolicyName}:{RateLimitKey.ForClient(IPAddress.Parse(ip))}";

    private HttpClient ProxiedClient(string clientIp)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote", DistributedRateLimiterWiringFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientIp);
        return client;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email = "nobody@example.com") =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { farmCode = TestHarness.DefaultFarmCode, email, password = "WrongPassw0rd!" });

    // (b) primary down → the WIRED limiter still serves (fallback), still
    // enforces a budget, and the alarm fires.
    [Fact]
    public async Task Primary_down_fallback_serves_and_enforces_and_alarms()
    {
        factory.Sink.Events.Clear();
        factory.Primary.SetDown(true);
        var client = ProxiedClient("203.0.113.210");

        for (var i = 0; i < DistributedRateLimiterWiringFactory.LoginLimit; i++)
        {
            var ok = await PostLoginAsync(client);
            // Served through the in-process fallback, NOT a 500.
            Assert.NotEqual(HttpStatusCode.InternalServerError, ok.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        // The fallback is still a real limiter: the next request 429s.
        var limited = await PostLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // The alarm fired: at least one SharedStateRedisUnavailable warning was
        // captured from the wired path (not the raw counter).
        var alarms = EventsFor(SecurityEvents.SharedStateRedisUnavailable);
        Assert.NotEmpty(alarms);
    }

    // (c) recovery without restart: primary set UP again (no host restart) and
    // a FRESH client IP → the primary stub observes that IP's key, i.e. traffic
    // returned to the shared path.
    [Fact]
    public async Task Primary_recovery_without_restart_returns_traffic_to_shared_path()
    {
        factory.Sink.Events.Clear();
        factory.Primary.SetDown(true);
        factory.Primary.ClearSeenKeys();

        var fallbackClient = ProxiedClient("203.0.113.211");
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(fallbackClient)).StatusCode);
        Assert.DoesNotContain(ExpectedKey("203.0.113.211"), factory.Primary.SeenKeys);

        // Redis "recovers" — no host restart, no new DI graph.
        factory.Primary.SetDown(false);

        var recoveredClient = ProxiedClient("203.0.113.212");
        var ok = await PostLoginAsync(recoveredClient);
        Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);

        Assert.Contains(ExpectedKey("203.0.113.212"), factory.Primary.SeenKeys);
    }

    // (d) limited before account lookup: a NON-EXISTENT user still 429s past
    // the limit — the limiter ran before/without a successful account lookup —
    // and every key the counter observed is the derived IP key.
    [Fact]
    public async Task Limit_fires_before_account_lookup_and_keys_are_the_ip_key_only()
    {
        factory.Primary.SetDown(false);
        factory.Primary.ClearSeenKeys();
        var clientIp = "203.0.113.213";
        var client = ProxiedClient(clientIp);

        for (var i = 0; i < DistributedRateLimiterWiringFactory.LoginLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(client)).StatusCode);
        var limited = await PostLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        var expectedKey = ExpectedKey(clientIp);
        Assert.NotEmpty(factory.Primary.SeenKeys);
        Assert.All(factory.Primary.SeenKeys, key => Assert.Equal(expectedKey, key));
    }

    // (e) no attacker-supplied dimension in key or log: the observed keys and
    // the RateLimitRejected event's properties carry the IP, never the email
    // from the attacker-controlled request body.
    [Fact]
    public async Task No_attacker_supplied_dimension_in_key_or_log()
    {
        factory.Sink.Events.Clear();
        factory.Primary.SetDown(false);
        factory.Primary.ClearSeenKeys();
        var clientIp = "203.0.113.214";
        var email = $"attacker-{Guid.NewGuid():N}@example.com";
        var client = ProxiedClient(clientIp);

        for (var i = 0; i < DistributedRateLimiterWiringFactory.LoginLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(client, email)).StatusCode);
        var limited = await PostLoginAsync(client, email);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Every key the shared counter saw is the derived IP key — none of
        // them contains the attacker-supplied email.
        Assert.NotEmpty(factory.Primary.SeenKeys);
        var expectedKey = ExpectedKey(clientIp);
        Assert.All(factory.Primary.SeenKeys, key => Assert.DoesNotContain(email, key));
        Assert.All(factory.Primary.SeenKeys, key => Assert.Equal(expectedKey, key));

        // And the auth security event itself: ClientIp is the forwarded IP,
        // Path is the route, and NO property value carries the email.
        var rejected = Assert.Single(EventsFor(SecurityEvents.RateLimitRejected));
        Assert.Equal(clientIp, ScalarOf(rejected, "ClientIp"));
        Assert.Contains("auth/login", ScalarOf(rejected, "Path"));
        foreach (var (name, value) in rejected.Properties)
        {
            if (value is not ScalarValue scalar) continue;
            var rendered = scalar.Value?.ToString() ?? "";
            Assert.DoesNotContain(email, rendered);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DistributedRateLimiterWiringCollection : ICollectionFixture<DistributedRateLimiterWiringFactory>
{
    public const string Name = "distributed-rate-limiter-wiring";
}
