namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

// #260 — behind a reverse proxy the app only honours X-Forwarded-Proto/-For from
// networks in RateLimiting:TrustedProxies. If that list is empty in Production,
// HSTS (#144) silently goes inert and the per-IP login rate limiter (#143)
// collapses to a single global bucket. A Production-only boot guard fails the
// SERVING boot loudly unless the operator either sets the proxy CIDR or opts out
// via RateLimiting:AllowNoTrustedProxies for a rare direct-TLS-exposure deploy.
//
// The base host boots in Production with empty proxies + the opt-out ON, so the
// fixture (and its InitializeAsync migration) succeed; the failure test derives a
// host that flips the opt-out OFF to observe the guard firing — mirroring
// SeedTimeZoneTests' good-fixture / bad-derived-host pattern.
public sealed class TrustedProxyOptOutFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // The guard is Production-only; the base factory runs "Testing". Override
        // to Production so the guard is actually in force for these tests.
        builder.UseEnvironment("Production");
        // Empty TrustedProxies (the base factory sets none) + opt-out ON: the
        // exact "direct-TLS-exposure" configuration that must be allowed to boot.
        builder.UseSetting("RateLimiting:AllowNoTrustedProxies", "true");
    }
}

public sealed class TrustedProxyGuardTests(TrustedProxyOptOutFactory factory)
    : IClassFixture<TrustedProxyOptOutFactory>
{
    private readonly TrustedProxyOptOutFactory _factory = factory;

    [Fact]
    public void EmptyTrustedProxies_InProduction_WithoutOptOut_FailsTheBoot()
    {
        // Flip the opt-out OFF while leaving TrustedProxies empty and the env
        // Production: the guard must fail the boot. Without the guard this host
        // would boot fine and Record.Exception would return null — that is the
        // red state this test is written to catch.
        using var badHost = _factory.WithWebHostBuilder(b =>
            b.UseSetting("RateLimiting:AllowNoTrustedProxies", "false"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        var message = Flatten(boot!);
        // The message must name both broken controls and both fixes so an
        // operator can act on it without reading the source.
        Assert.Contains("TrustedProxies", message);
        Assert.Contains("#144", message);            // HSTS
        Assert.Contains("#143", message);            // per-IP login limiter
        Assert.Contains("AllowNoTrustedProxies", message);
    }

    [Fact]
    public async Task EmptyTrustedProxies_InProduction_WithOptOut_Boots()
    {
        // The class-fixture host itself: Production + empty proxies + opt-out ON.
        // It must boot and serve — the deliberate direct-exposure escape hatch.
        var client = _factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public void NonEmptyTrustedProxies_InProduction_Boots()
    {
        // A configured proxy CIDR satisfies the guard even with the opt-out OFF —
        // proving it is the CIDR, not the opt-out, that clears the check.
        using var host = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:AllowNoTrustedProxies", "false");
            b.UseSetting("RateLimiting:TrustedProxies:0", "172.16.0.0/12");
        });

        var boot = Record.Exception(() => host.CreateClient());

        Assert.Null(boot);
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add(e.Message);
        return string.Join(" | ", parts);
    }
}
