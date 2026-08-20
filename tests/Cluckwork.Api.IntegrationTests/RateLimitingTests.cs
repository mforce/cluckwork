namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// #143 — the anonymous auth endpoints are rate limited per client IP, resolved
// by the framework ForwardedHeaders middleware. The in-process TestServer has
// no socket peer, so this factory injects one via a test header (X-Test-Remote)
// and trusts a fixed proxy address; requests can then present either as coming
// through the trusted proxy (X-Forwarded-For honored) or as an untrusted direct
// caller (X-Forwarded-For ignored — the anti-spoof boundary).
public sealed class RateLimitFactory : CluckworkWebApplicationFactory
{
    public const string TrustedProxy = "10.99.0.1";
    public const int LoginLimit = 3;
    public const int RefreshLimit = 5;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Login:PermitLimit", LoginLimit.ToString());
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "900");
        builder.UseSetting("RateLimiting:Refresh:PermitLimit", RefreshLimit.ToString());
        builder.UseSetting("RateLimiting:Refresh:WindowSeconds", "900");
        builder.UseSetting("RateLimiting:TrustedProxies:0", $"{TrustedProxy}/32");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
    }
}

public sealed class RateLimitingTests : IClassFixture<RateLimitFactory>
{
    private readonly RateLimitFactory _factory;

    public RateLimitingTests(RateLimitFactory factory) => _factory = factory;

    // A caller arriving through the trusted proxy: its X-Forwarded-For is the
    // real client IP the limiter buckets by.
    private HttpClient ProxiedClient(string clientIp)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote", RateLimitFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientIp);
        return client;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode = TestHarness.DefaultFarmCode, email = "nobody@example.com", password = "WrongPassw0rd!" });

    // #145 — refresh reads the token from the cookie and needs the CSRF header;
    // with the header but no cookie it still lands on 401 within the limit (and
    // 429 over it), which is all this rate-limit probe needs.
    private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Login_within_limit_is_unaffected_and_over_limit_returns_429_problem()
    {
        var client = ProxiedClient("203.0.113.10");

        for (var i = 0; i < RateLimitFactory.LoginLimit; i++)
        {
            var ok = await PostLoginAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        var limited = await PostLoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"),
            "429 must carry a Retry-After header");
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 900);
        Assert.Equal("application/problem+json",
            limited.Content.Headers.ContentType?.MediaType);
        var problem = await limited.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
    }

    // #309 Fix 2 — the body-limit middleware used to run BEFORE the rate
    // limiter, so a declared-oversize login body 413'd and `return`ed before
    // UseRateLimiter ever consumed a permit: an attacker could flood oversized
    // bodies at unlimited rate while every legitimate-sized attempt was
    // throttled. It now runs AFTER UseRateLimiter, so an oversized body still
    // draws from the same bucket a legitimate attempt would.
    [Fact]
    public async Task Oversized_login_body_still_consumes_a_rate_limit_permit()
    {
        var client = ProxiedClient("203.0.113.70");

        for (var i = 0; i < RateLimitFactory.LoginLimit; i++)
        {
            var oversized = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { farmCode = TestHarness.DefaultFarmCode, email = "nobody@example.com", password = new string('a', 8192) });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        }

        // The bucket is exhausted purely by oversized-body attempts — the next
        // request, even a normal-sized one, must 429 rather than reach the
        // handler.
        var limited = await PostLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Refresh_has_its_own_bucket_independent_of_login()
    {
        var client = ProxiedClient("203.0.113.20");

        // Exhaust the login bucket entirely.
        for (var i = 0; i < RateLimitFactory.LoginLimit; i++)
            await PostLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client)).StatusCode);

        // Refresh from the same IP is unaffected — a separate, looser bucket —
        // until its own limit, then 429. This is the NAT-starvation guard.
        for (var i = 0; i < RateLimitFactory.RefreshLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostRefreshAsync(client)).StatusCode);
    }

    [Fact]
    public async Task Distinct_client_ips_have_independent_buckets()
    {
        var first = ProxiedClient("203.0.113.30");
        for (var i = 0; i < RateLimitFactory.LoginLimit; i++)
            await PostLoginAsync(first);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(first)).StatusCode);

        var second = ProxiedClient("203.0.113.31");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Untrusted_direct_caller_cannot_spoof_its_bucket_via_forwarded_header()
    {
        // Socket peer is NOT a trusted proxy, so X-Forwarded-For is ignored and
        // the limiter keys on the socket IP. Rotating the header must not mint a
        // fresh bucket — otherwise the whole control is bypassable.
        var untrusted = "198.51.100.200";

        HttpClient Spoofing(string forgedClientIp)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Remote", untrusted);
            client.DefaultRequestHeaders.Add("X-Forwarded-For", forgedClientIp);
            return client;
        }

        for (var i = 0; i < RateLimitFactory.LoginLimit; i++)
            await PostLoginAsync(Spoofing($"1.2.3.{i}"));

        // A new forged header value — still the same untrusted socket IP bucket.
        var limited = await PostLoginAsync(Spoofing("9.9.9.9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Logout_is_not_rate_limited()
    {
        var client = ProxiedClient("203.0.113.40");

        // Hammer logout well past the login limit; it must never 429, so an
        // exhausted login bucket can't block logout. Logout is anonymous +
        // cookie-authenticated (#145): with no CSRF header it lands on 403 — the
        // point is it reaches the handler and is never rate-limited.
        for (var i = 0; i < RateLimitFactory.LoginLimit + 3; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/logout",
                new { refreshToken = "whatever" });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, res.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
    }

    [Fact]
    public async Task Non_auth_endpoints_are_not_limited_by_the_auth_policy()
    {
        var client = ProxiedClient("203.0.113.50");

        for (var i = 0; i < RateLimitFactory.LoginLimit; i++)
            await PostLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client)).StatusCode);

        // Same client: non-auth routes keep working.
        for (var i = 0; i < 5; i++)
        {
            var health = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }
}
