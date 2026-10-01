namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
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
    // #873 — the one token in the policy that changes per response. Pulled out
    // by pattern rather than by offset so a directive added ahead of style-src
    // cannot silently shift what this reads.
    private static readonly Regex StyleNoncePattern =
        new(@"style-src 'self' 'nonce-(?<nonce>[^']+)'", RegexOptions.Compiled);

    internal static string StyleNonce(string csp)
    {
        var match = StyleNoncePattern.Match(csp);
        Assert.True(match.Success, $"no style-src nonce in the policy: {csp}");
        return match.Groups["nonce"].Value;
    }

    // #874 review (local Codex pass): written independently of
    // SecurityHeaders.BuildContentSecurityPolicy rather than calling it, so a
    // directive dropped, reordered, or widened in the implementation changes
    // only ONE side of the comparison below. Calling the builder on both sides
    // (the prior version of this test) meant a deleted directive vanished from
    // both the response and the "expected" value identically, and nothing in
    // the file would have caught it — confirmed by mutation: removing
    // `form-action 'self'` from the builder left this test green until the
    // string below was pinned by hand.
    private static string ExpectedContentSecurityPolicy(string styleNonce) =>
        "default-src 'self'; "
        + "script-src 'self'; "
        + $"style-src 'self' 'nonce-{styleNonce}'; "
        + "img-src 'self' blob:; "
        + "font-src 'self'; "
        + "connect-src 'self'; "
        + "frame-src 'none'; "
        + "worker-src 'self'; "
        + "frame-ancestors 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "object-src 'none'";

    [Theory]
    [InlineData("/health/live")]              // a normal 200
    [InlineData("/definitely-not-a-route")]   // a 404 — headers come from OnStarting, so still present
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var res = await factory.CreateClient().GetAsync(path);

        // The WHOLE policy, not a set of Contains checks: compared against an
        // independently written expected string (#874 review) with only the
        // nonce substituted, so any other directive that changed — added,
        // dropped, reordered, widened — fails here (#873).
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal(ExpectedContentSecurityPolicy(StyleNonce(csp)), csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Style_src_keeps_self_and_adds_exactly_one_nonce()
    {
        var csp = (await factory.CreateClient().GetAsync("/health/live"))
            .Headers.GetValues("Content-Security-Policy").Single();

        // #873 — the COMPLETE token set. 'self' has to stay (styles.css and the
        // Inter font CSS are same-origin links a nonce does not cover) and the
        // nonce has to be there (MUI's Emotion styles are injected at runtime).
        // A set assertion is what fails if a later change drops either, or
        // reaches for 'unsafe-inline' to make a symptom go away.
        var styleSrc = csp
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Single(d => d.StartsWith("style-src ", StringComparison.Ordinal));
        var tokens = styleSrc.Split(' ');
        Assert.Equal(3, tokens.Length);
        Assert.Equal("style-src", tokens[0]);
        Assert.Equal("'self'", tokens[1]);
        Assert.StartsWith("'nonce-", tokens[2], StringComparison.Ordinal);

        // A cryptographic nonce, not a counter or a constant: at least 16
        // decoded bytes (128 bits, CSP's own floor for "unguessable"),
        // base64-encoded. A literal minimum, not SecurityHeaders.NonceByteCount
        // itself (#874 review round 2, local Codex pass): comparing against the
        // implementation's own constant meant shrinking NonceByteCount from 16
        // to 4 changed both sides of the assertion identically and stayed
        // green — confirmed by mutation, reverted after.
        var decodedNonce = Convert.FromBase64String(StyleNonce(csp));
        Assert.True(decodedNonce.Length >= 16,
            $"nonce must decode to at least 16 bytes (128 bits), got {decodedNonce.Length}");

        // Nowhere else. script-src taking a nonce would be a far larger
        // concession than this change makes, and it must not ride along.
        Assert.Equal(1, csp.Split("'nonce-").Length - 1);
    }

    [Fact]
    public async Task Each_response_mints_a_fresh_nonce()
    {
        var client = factory.CreateClient();

        var first = StyleNonce((await client.GetAsync("/health/live"))
            .Headers.GetValues("Content-Security-Policy").Single());
        var second = StyleNonce((await client.GetAsync("/health/live"))
            .Headers.GetValues("Content-Security-Policy").Single());

        // A nonce reused across responses is not a nonce: an attacker who reads
        // one page's value could then author a <style> the next page admits.
        Assert.NotEqual(first, second);
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
    public async Task Csp_allows_blob_images_and_nothing_else_off_origin()
    {
        var csp = (await factory.CreateClient().GetAsync("/health/live"))
            .Headers.GetValues("Content-Security-Policy").Single();

        // #123: the farm logo is auth-gated, so the SPA fetches the bytes and
        // renders them from an object URL — which img-src has to admit.
        //
        // The COMPLETE token set, not a prefix: `Contains` alone would pass a
        // policy that had grown to `img-src 'self' blob: https:` or picked up a
        // named remote origin (codex review of #123).
        var imgSrc = csp
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Single(d => d.StartsWith("img-src ", StringComparison.Ordinal));
        Assert.Equal(new[] { "img-src", "'self'", "blob:" }, imgSrc.Split(' '));

        // The concession is exactly one scheme on exactly one directive: one
        // occurrence in the whole policy, and it is the one asserted above. So
        // copying `blob:` onto script-src — where it is an eval-shaped hole,
        // since a blob URL can carry any code this page cares to write — fails
        // here rather than shipping.
        Assert.Equal(1, csp.Split("blob:").Length - 1);

        // Nothing else was loosened alongside it.
        Assert.DoesNotContain("*", csp);
        Assert.DoesNotContain("data:", csp);
        foreach (var directive in new[] { "default-src", "script-src", "font-src", "connect-src" })
            Assert.Contains($"{directive} 'self';", csp);
        // style-src is the one that is not a bare 'self' any more (#873); its
        // exact token set is asserted in Style_src_keeps_self_and_adds_exactly_one_nonce.
        Assert.Contains("style-src 'self' 'nonce-", csp);
    }

    [Fact]
    public async Task Csp_admits_a_same_origin_service_worker_and_no_other()
    {
        var csp = (await factory.CreateClient().GetAsync("/health/live"))
            .Headers.GetValues("Content-Security-Policy").Single();

        // #142: the PWA worker is a same-origin /sw.js. worker-src 'none' would
        // block register() outright — and silently, since a blocked
        // registration only rejects a promise, so the app would look fine while
        // never caching or updating. The COMPLETE token set, not a prefix: this
        // fails if the directive is ever widened to a scheme or a remote origin
        // rather than staying same-origin-only.
        var workerSrc = csp
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Single(d => d.StartsWith("worker-src ", StringComparison.Ordinal));
        Assert.Equal(new[] { "worker-src", "'self'" }, workerSrc.Split(' '));
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
            //
            // Clearing it is also what this factory CANNOT assert: with the
            // exclusion list emptied, the wrong-host direction — the header
            // emitted for a host that should never receive a one-year
            // commitment — is unobservable here, and so is the scheme gate,
            // since the TestServer has no transport and `X-Forwarded-Proto`
            // stands in for HTTPS. Both now run over a real TLS socket in
            // HstsOverRealTlsTests (#344), which leaves ExcludedHosts alone and
            // varies the request host instead. What remains unverifiable
            // in-process is nothing about this middleware; it is whether a
            // production edge forwards a scheme the app trusts, which is a
            // deploy concern (#260's boot guard, cluckwork-deploy#8).
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
        // stays hermetic — no stray connection string.
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
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
