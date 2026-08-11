namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

// #319 — appsettings.json defaults AllowedHosts to "*"; a Production deploy that
// omits or misnames the host variable (a blank ${CLUCKWORK_HOST} substitution was
// observed) silently disables Host-header filtering (#144) and a forged Host is
// accepted. A Production-only SERVING boot guard fails loudly unless a concrete
// public host is pinned. The base factory runs "Testing" (guard dormant); this
// factory flips to Production with a concrete host so the good path boots, and the
// failure cases derive a host that removes the concrete value — mirroring
// TrustedProxyGuardTests. That the guard leaves the one-shot CLI verbs alone is
// no longer a matter of where it sits (#347 moved it ahead of the dispatcher and
// gave it an explicit ProcessRole check): it is covered by ProcessRoleGuardTests,
// and by MigrateCommandTests, which already runs in Production with no
// AllowedHosts set — i.e. the appsettings "*" wildcard — and must stay green.
public sealed class AllowedHostsProductionFactory : CluckworkWebApplicationFactory
{
    public const string PublicHost = "cluckwork.example";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Guard is Production-only; the base factory runs "Testing".
        builder.UseEnvironment("Production");
        builder.UseSetting("AllowedHosts", PublicHost);
    }
}

public sealed class AllowedHostsGuardTests(AllowedHostsProductionFactory factory)
    : IClassFixture<AllowedHostsProductionFactory>
{
    private readonly AllowedHostsProductionFactory _factory = factory;

    private static async Task<HttpStatusCode> GetWithHostAsync(HttpClient client, string host)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Host = host;
        return (await client.SendAsync(req)).StatusCode;
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    [Theory]
    [InlineData("*")]                    // explicit wildcard — filtering off
    [InlineData("")]                     // blank ${CLUCKWORK_HOST} substitution → no host
    [InlineData("  ")]                   // whitespace-only → no host
    [InlineData("*;cluckwork.example")]  // a wildcard alongside a host is still wide open
    public void NoConcretePublicHost_InProduction_FailsTheBoot(string allowedHosts)
    {
        // Without the guard this host would boot fine and Record.Exception would
        // return null — the red state this test is written to catch.
        using var badHost = _factory.WithWebHostBuilder(b =>
            b.UseSetting("AllowedHosts", allowedHosts));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        var message = Flatten(boot!);
        // Name the setting and the canonical deploy key so an operator can act
        // without reading the source.
        Assert.Contains("AllowedHosts", message);
        Assert.Contains("CLUCKWORK_HOST", message);
    }

    [Fact]
    public async Task ConcretePublicHost_InProduction_Boots_AndFiltersHostHeader()
    {
        // The class-fixture host: Production + a concrete pinned host. It boots,
        // serves the pinned host, rejects a forged Host (400 — filtering is truly
        // active in Production, not just Testing), and still answers the loopback
        // health probe (the documented allowance for container health checks).
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await GetWithHostAsync(client, AllowedHostsProductionFactory.PublicHost));
        Assert.Equal(HttpStatusCode.BadRequest, await GetWithHostAsync(client, "evil.example"));
        Assert.Equal(HttpStatusCode.OK, await GetWithHostAsync(client, "localhost"));
    }
}
