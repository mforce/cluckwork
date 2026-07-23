namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Generic;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// #144 — every response carries the static security headers (CSP, nosniff,
// Referrer-Policy, frame protection), regardless of role, auth, or whether the
// route even exists. Uses the shared collection factory (no special config).
[Collection(IntegrationCollection.Name)]
public sealed class SecurityHeadersTests(CluckworkWebApplicationFactory factory)
{
    [Theory]
    [InlineData("/health/live")]              // a normal 200
    [InlineData("/definitely-not-a-route")]   // a 404 — headers come from OnStarting, so still present
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var res = await factory.CreateClient().GetAsync(path);

        Assert.Equal(SecurityHeaders.ContentSecurityPolicy,
            res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Csp_blocks_inline_script_and_framing()
    {
        var csp = (await factory.CreateClient().GetAsync("/health/live"))
            .Headers.GetValues("Content-Security-Policy").Single();

        // script-src has no 'unsafe-inline'/nonce/hash (the pre-paint theme
        // script was externalised for exactly this), and framing is denied.
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
    }

    [Fact]
    public async Task Hsts_is_absent_on_plain_http()
    {
        // No forwarded proto → the request is http → HSTS must not be emitted.
        var res = await factory.CreateClient().GetAsync("/health/live");
        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }
}

// Injects a socket peer (TestServer has none) so the framework ForwardedHeaders
// middleware will honour X-Forwarded-Proto/For from the trusted proxy address.
internal sealed class FakeRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMw) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                && IPAddress.TryParse(remote.ToString(), out var ip))
                ctx.Connection.RemoteIpAddress = ip;
            await nextMw();
        });
        next(app);
    };
}

// Trusts a fixed proxy and configures an https port, so we can prove that a
// trusted X-Forwarded-Proto: https both enables HSTS and prevents the HTTPS
// redirect (the redirect-loop root cause #144 addresses).
public sealed class SecurityProxyFactory : CluckworkWebApplicationFactory
{
    public const string TrustedProxy = "10.99.0.1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:TrustedProxies:0", $"{TrustedProxy}/32");
        builder.UseSetting("https_port", "443"); // so HttpsRedirection has a port to redirect to
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
            // HSTS's default ExcludedHosts skips loopback; the TestServer speaks
            // to localhost, so clear it to observe the header (a real deployment's
            // public host is never excluded, so production is unaffected).
            services.Configure<Microsoft.AspNetCore.HttpsPolicy.HstsOptions>(o => o.ExcludedHosts.Clear());
        });
    }
}

public sealed class SecurityHeadersForwardedProxyTests(SecurityProxyFactory factory)
    : IClassFixture<SecurityProxyFactory>
{
    private HttpClient NoRedirectClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private HttpClient ForwardedHttpsClient()
    {
        var client = NoRedirectClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote", SecurityProxyFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }

    [Fact]
    public async Task Hsts_present_outside_development_over_forwarded_https()
    {
        var res = await ForwardedHttpsClient().GetAsync("/health/live");

        var hsts = res.Headers.GetValues("Strict-Transport-Security").Single();
        Assert.Contains("max-age=31536000", hsts); // one year
        Assert.Contains("includeSubDomains", hsts);
    }

    [Fact]
    public async Task Forwarded_https_is_not_redirected_but_plain_http_is()
    {
        // Control: without the forwarded proto the app sees http and the
        // configured https port makes HttpsRedirection issue a redirect.
        var plain = await NoRedirectClient().GetAsync("/health/live");
        Assert.Contains(plain.StatusCode,
            new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.TemporaryRedirect,
                    HttpStatusCode.PermanentRedirect, HttpStatusCode.Found });

        // Trusted forwarded https → the app already sees https → no redirect loop.
        var forwarded = await ForwardedHttpsClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
    }
}

// Development environment (kept hermetic against the developer's user-secrets by
// pinning the security-relevant config to an in-memory source of highest
// precedence) to prove HSTS is suppressed in Development.
public sealed class SecurityDevelopmentFactory : CluckworkWebApplicationFactory
{
    public const string TrustedProxy = "10.99.0.2";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting("RateLimiting:TrustedProxies:0", $"{TrustedProxy}/32");
        builder.UseSetting("https_port", "443");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
        // Development loads the machine's user-secrets; override the sensitive
        // keys from a last-added (highest-precedence) source so the test host
        // stays hermetic — no machine seed creds, no stray connection string.
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Seed:Enabled"] = "false",
                ["ConnectionStrings:Default"] = ConnectionString,
                ["Jwt:PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem,
                ["Jwt:PublicKeyPem"] = TestJwtKeys.PublicKeyPem,
                ["Jwt:Issuer"] = "cluckwork-test",
                ["Jwt:Audience"] = "cluckwork-api-test",
            }));
    }
}

public sealed class SecurityHeadersDevelopmentTests(SecurityDevelopmentFactory factory)
    : IClassFixture<SecurityDevelopmentFactory>
{
    [Fact]
    public async Task Hsts_absent_in_development_even_over_forwarded_https()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Remote", SecurityDevelopmentFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var res = await client.GetAsync("/health/live");

        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
        // The CSP still applies in Development — it is not env-gated.
        Assert.True(res.Headers.Contains("Content-Security-Policy"));
    }
}

// Pins AllowedHosts to a public hostname; the framework host-filtering
// middleware must then reject a forged Host header (400) while the container's
// loopback health probe keeps working.
public sealed class PinnedHostFactory : CluckworkWebApplicationFactory
{
    public const string PublicHost = "cluckwork.example";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("AllowedHosts", PublicHost);
    }
}

public sealed class HostPinningTests(PinnedHostFactory factory) : IClassFixture<PinnedHostFactory>
{
    private async Task<HttpStatusCode> GetWithHostAsync(string host)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Host = host;
        return (await factory.CreateClient().SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Unexpected_host_is_rejected()
        => Assert.Equal(HttpStatusCode.BadRequest, await GetWithHostAsync("evil.example"));

    [Fact]
    public async Task Pinned_public_host_is_accepted()
        => Assert.Equal(HttpStatusCode.OK, await GetWithHostAsync(PinnedHostFactory.PublicHost));

    [Fact]
    public async Task Loopback_health_probe_still_works()
        => Assert.Equal(HttpStatusCode.OK, await GetWithHostAsync("localhost"));
}
