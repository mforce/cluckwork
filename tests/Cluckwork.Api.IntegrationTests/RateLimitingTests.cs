namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

// #143 — the anonymous auth endpoints are rate limited per client IP. These
// tests run against their own factory with a tight limit (3/window) so they
// don't fight the shared collection factory, and partition by X-Forwarded-For
// (the in-process TestServer has no socket address, which the resolver treats
// as trusted — mirroring the reverse-proxy deployment).
public sealed class RateLimitFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "3");
        builder.UseSetting("RateLimiting:Auth:WindowSeconds", "900");
    }
}

public sealed class RateLimitingTests : IClassFixture<RateLimitFactory>
{
    private readonly RateLimitFactory _factory;

    public RateLimitingTests(RateLimitFactory factory) => _factory = factory;

    private HttpClient ClientFrom(string ip)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return client;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "nobody@example.com", password = "WrongPassw0rd!" });

    private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = "bogus-refresh-token" });

    [Fact]
    public async Task Login_within_limit_is_unaffected_and_over_limit_returns_429_problem()
    {
        var client = ClientFrom("203.0.113.10");

        for (var i = 0; i < 3; i++)
        {
            var ok = await PostLoginAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        var limited = await PostLoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"),
            "429 must carry a Retry-After header");
        Assert.Equal("application/problem+json",
            limited.Content.Headers.ContentType?.MediaType);
        var problem = await limited.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Refresh_over_limit_returns_429()
    {
        var client = ClientFrom("203.0.113.11");

        for (var i = 0; i < 3; i++)
        {
            var ok = await PostRefreshAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        var limited = await PostRefreshAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Distinct_client_ips_have_independent_buckets()
    {
        var first = ClientFrom("203.0.113.12");
        for (var i = 0; i < 4; i++)
            await PostLoginAsync(first);
        var exhausted = await PostLoginAsync(first);
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        var second = ClientFrom("203.0.113.13");
        var fresh = await PostLoginAsync(second);
        Assert.Equal(HttpStatusCode.Unauthorized, fresh.StatusCode);
    }

    [Fact]
    public async Task Non_auth_endpoints_are_not_limited_by_the_auth_policy()
    {
        var client = ClientFrom("203.0.113.14");

        for (var i = 0; i < 4; i++)
            await PostLoginAsync(client);
        var exhausted = await PostLoginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        // Same "client": non-auth routes keep working.
        for (var i = 0; i < 5; i++)
        {
            var health = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }
}
