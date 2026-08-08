namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// #343 — RateLimitingTests and SecurityHeadersForwardedProxyTests prove the
// forwarded-header trust decision using FakeRemoteIpStartupFilter, which
// synthesizes Connection.RemoteIpAddress from a test header because the
// in-process TestServer has no socket peer at all. That proves the app's
// CONFIGURATION is wired correctly, but the trust comparison itself —
// ForwardedHeadersOptions.KnownIPNetworks matched against a real
// Connection.RemoteIpAddress — is never actually exercised: the filter's fake
// peer stands in for it, and a fake peer can be told to say anything,
// including the untrusted case's negative.
//
// These run over UseKestrel(0) (#340/#341) instead: a real socket, so the
// framework's ForwardedHeadersMiddleware matches against the genuine peer
// address Kestrel reports. Kestrel always binds and connects over 127.0.0.1,
// so every request in this file shares the same real peer — to exercise both
// sides of the trust decision, vary the TRUSTED-NETWORKS CONFIG per factory
// (as the issue's scope note directs) rather than the peer address, which
// can't be varied over a real connection.
public sealed class TrustedPeerKestrelFactory : CluckworkWebApplicationFactory
{
    public const int LoginLimit = 3;

    public TrustedPeerKestrelFactory() => UseKestrel(0);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Login:PermitLimit", LoginLimit.ToString());
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "900");
        // The real Kestrel peer (127.0.0.1) IS a trusted proxy.
        builder.UseSetting("RateLimiting:TrustedProxies:0", "127.0.0.1/32");
        builder.ConfigureTestServices(services =>
            // HSTS's default ExcludedHosts skips loopback; the Kestrel client
            // talks to 127.0.0.1, so clear it to observe the header (a real
            // deployment's public host is never excluded).
            services.Configure<HstsOptions>(o => o.ExcludedHosts.Clear()));
    }
}

public sealed class UntrustedPeerKestrelFactory : CluckworkWebApplicationFactory
{
    public const int LoginLimit = 3;

    public UntrustedPeerKestrelFactory() => UseKestrel(0);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Login:PermitLimit", LoginLimit.ToString());
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "900");
        // A network that does NOT include the real Kestrel peer: forwarded
        // headers presented over this connection must be ignored entirely —
        // the negative FakeRemoteIpStartupFilter cannot express, since it can
        // fabricate any peer address it likes rather than reporting the real one.
        builder.UseSetting("RateLimiting:TrustedProxies:0", "10.99.0.0/24");
        builder.ConfigureTestServices(services =>
            services.Configure<HstsOptions>(o => o.ExcludedHosts.Clear()));
    }
}

// Two trusted networks: the real Kestrel peer (127.0.0.1) AND a simulated
// intermediate proxy (10.50.0.0/24) that can appear as a header entry. Proves
// ForwardLimit=null actually walks MORE than one hop when trust allows it —
// which a single-trusted-hop factory (the two above) cannot distinguish from
// the framework's own default ForwardLimit of 1, since with only one trusted
// hop both configurations behave identically.
public sealed class TrustedTwoHopKestrelFactory : CluckworkWebApplicationFactory
{
    public const int LoginLimit = 3;

    public TrustedTwoHopKestrelFactory() => UseKestrel(0);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Login:PermitLimit", LoginLimit.ToString());
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "900");
        builder.UseSetting("RateLimiting:TrustedProxies:0", "127.0.0.1/32");
        builder.UseSetting("RateLimiting:TrustedProxies:1", "10.50.0.0/24");
    }
}

file static class ForwardedRequests
{
    public static Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string? forwardedFor = null, string? forwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@example.com", password = "WrongPassw0rd!" })
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null) request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        return client.SendAsync(request);
    }

    // The parameterless CreateClient() is the only overload that rewrites the
    // base address to the bound Kestrel port (TestHarness.cs), so an options
    // object must copy it explicitly or the client points at http://localhost/
    // and reaches nothing.
    public static HttpClient NoRedirectClient(CluckworkWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = factory.ClientOptions.BaseAddress,
        });
}

public sealed class TrustedPeerForwardedHeaderTests(TrustedPeerKestrelFactory factory)
    : IClassFixture<TrustedPeerKestrelFactory>
{
    [Fact]
    public async Task Forwarded_for_from_the_real_trusted_peer_partitions_by_the_forwarded_client_ip()
    {
        var a = factory.CreateClient();
        for (var i = 0; i < TrustedPeerKestrelFactory.LoginLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await ForwardedRequests.PostLoginAsync(a, forwardedFor: "203.0.113.10")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await ForwardedRequests.PostLoginAsync(a, forwardedFor: "203.0.113.10")).StatusCode);

        // A different forwarded client IP, presented over the SAME real
        // Kestrel peer, must be an independent bucket.
        var b = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ForwardedRequests.PostLoginAsync(b, forwardedFor: "203.0.113.11")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_https_from_the_real_trusted_peer_enables_hsts()
    {
        var client = ForwardedRequests.NoRedirectClient(factory);

        var res = await ForwardedRequests.PostLoginAsync(client, forwardedProto: "https");

        Assert.True(res.Headers.Contains("Strict-Transport-Security"));
    }
}

public sealed class UntrustedPeerForwardedHeaderTests(UntrustedPeerKestrelFactory factory)
    : IClassFixture<UntrustedPeerKestrelFactory>
{
    // The security-relevant negative: without this, any client could forge its
    // source IP over an untrusted connection and defeat the per-IP login
    // limiter entirely (#143 credential-stuffing bypass).
    [Fact]
    public async Task Forwarded_for_from_an_untrusted_real_peer_is_ignored_so_forged_headers_share_one_bucket()
    {
        var a = factory.CreateClient();
        for (var i = 0; i < UntrustedPeerKestrelFactory.LoginLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await ForwardedRequests.PostLoginAsync(a, forwardedFor: "1.2.3.4")).StatusCode);

        // A fresh client presenting a DIFFERENT forged X-Forwarded-For, over
        // the same untrusted real socket peer, must land in the SAME bucket —
        // the header is not honoured at all over an untrusted connection.
        var b = factory.CreateClient();
        var limited = await ForwardedRequests.PostLoginAsync(b, forwardedFor: "9.9.9.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Forwarded_https_from_an_untrusted_real_peer_does_not_enable_hsts()
    {
        var client = ForwardedRequests.NoRedirectClient(factory);

        var res = await ForwardedRequests.PostLoginAsync(client, forwardedProto: "https");

        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }
}

public sealed class TrustedMultiHopForwardedHeaderTests(TrustedTwoHopKestrelFactory factory)
    : IClassFixture<TrustedTwoHopKestrelFactory>
{
    [Fact]
    public async Task Forward_limit_null_walks_past_a_trusted_intermediate_hop_to_the_original_client()
    {
        // Chain: "<original client>, <trusted intermediate proxy>". The real
        // Kestrel peer (127.0.0.1) is trusted, so the middleware adopts the
        // rightmost entry (10.50.0.7); that address is ALSO a trusted network
        // here, so with ForwardLimit=null it keeps walking and adopts the next
        // entry left — the original client, 203.0.113.99. A ForwardLimit of
        // the framework's own default (1) would have stopped one hop earlier,
        // leaving RemoteIpAddress pinned at the intermediate proxy instead.
        var a = factory.CreateClient();
        for (var i = 0; i < TrustedTwoHopKestrelFactory.LoginLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await ForwardedRequests.PostLoginAsync(a, forwardedFor: "203.0.113.99, 10.50.0.7")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await ForwardedRequests.PostLoginAsync(a, forwardedFor: "203.0.113.99, 10.50.0.7")).StatusCode);

        // A different ORIGINAL client, through the identical trusted
        // intermediate hop, must be an independent bucket — proving the
        // resolved address is the original client and not the (here,
        // identical-across-both) intermediate proxy.
        var b = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ForwardedRequests.PostLoginAsync(b, forwardedFor: "203.0.113.100, 10.50.0.7")).StatusCode);
    }
}
