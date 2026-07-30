namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// #266 — the `healthcheck` verb's probe logic. No Docker: each test stands up a
// tiny REAL Kestrel returning a chosen status, so it exercises a real socket +
// real HTTP (not a mocked handler). The mapping guarantee: a 2xx → exit 0, and
// EVERY other outcome (non-2xx status, connection refused, timeout) → exit 1 —
// a probe that can't get a healthy answer must report UNHEALTHY, never a false
// green. Not in the shared IntegrationCollection: it needs no Postgres fixture.
public sealed class HealthCheckCliCommandTests
{
    // Starts a throwaway HTTP server whose /health/ready runs `handler`, bound to
    // an ephemeral loopback port. Returns the app (dispose to stop) and the
    // fully-qualified probe URL with the resolved port.
    private static async Task<(WebApplication App, string Url)> StartStubAsync(Delegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/health/ready", handler);
        await app.StartAsync();
        return (app, $"{app.Urls.First()}/health/ready");
    }

    private static Task<int> ProbeAsync(string url, TimeSpan timeout) =>
        HealthCheckCliCommand.ProbeAsync(url, timeout);

    [Fact]
    public async Task Probe_Returns0_OnHttp200()
    {
        var (app, url) = await StartStubAsync(() => Results.StatusCode(StatusCodes.Status200OK));
        await using (app)
            Assert.Equal(0, await ProbeAsync(url, TimeSpan.FromSeconds(5)));
    }

    // The load-bearing case: 503 is exactly what /health/ready returns while the
    // DB is down or migrations are pending. A "any response is fine" bug would
    // let this pass — it must map to exit 1.
    [Fact]
    public async Task Probe_Returns1_OnHttp503()
    {
        var (app, url) = await StartStubAsync(() => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        await using (app)
            Assert.Equal(1, await ProbeAsync(url, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Probe_Returns1_WhenConnectionRefused()
    {
        // Port 1 on loopback: nothing listens, so connect fails fast → unhealthy,
        // never a false green from an unreachable server.
        Assert.Equal(1, await ProbeAsync("http://127.0.0.1:1/health/ready", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Probe_Returns1_WhenServerExceedsTimeout()
    {
        // Responds far slower than the probe timeout → the probe must give up and
        // report unhealthy rather than hang.
        var (app, url) = await StartStubAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return Results.Ok();
        });
        await using (app)
            Assert.Equal(1, await ProbeAsync(url, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Probe_Returns1_OnRedirect_WithoutFollowingToAFalseGreen()
    {
        // /health/ready 302s to a route that returns 200 (as the SPA shell would).
        // The probe must NOT follow it: a 3xx away from readiness is not-ready.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/health/ready", () => Results.Redirect("/shell"));
        app.MapGet("/shell", () => Results.Ok());
        await app.StartAsync();
        await using (app)
            // With AllowAutoRedirect on (the bug) this would follow 302→/shell→200→0.
            Assert.Equal(1, await ProbeAsync($"{app.Urls.First()}/health/ready", TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(null, 8080)]                              // unset → EXPOSE/compose contract
    [InlineData("", 8080)]                                // empty → default
    [InlineData("http://+:8080", 8080)]                   // the container's own value
    [InlineData("http://*:6000", 6000)]                   // '*' wildcard host
    [InlineData("http://0.0.0.0:5005", 5005)]            // custom port honoured
    [InlineData("http://localhost:9000/", 9000)]         // trailing slash tolerated
    [InlineData("http://localhost", 80)]                 // port omitted → http default 80
    [InlineData("http://[::1]:8080", 8080)]              // IPv6 loopback
    [InlineData("https://+:8081;http://+:9100", 9100)]   // https skipped, http used
    [InlineData("https://+:8081", 8080)]                 // https-only → no http port → default
    public void DefaultReadyUrl_DerivesPortFromAspnetcoreUrls(string? urls, int expectedPort)
    {
        Assert.Equal($"http://localhost:{expectedPort}/health/ready",
            HealthCheckCliCommand.DefaultReadyUrl(urls));
    }

    [Theory]
    [InlineData(null, "9090", 9090)]              // ASPNETCORE_HTTP_PORTS used when URLS unset
    [InlineData(null, "8080;8081", 8080)]         // first bare port wins
    [InlineData("http://+:7000", "9090", 7000)]   // ASPNETCORE_URLS takes precedence
    [InlineData(null, null, 8080)]                // both unset → default
    [InlineData(null, "   ", 8080)]               // blank → default
    [InlineData(null, "notaport", 8080)]          // junk → default
    public void DefaultReadyUrl_FallsBackToHttpPortsWhenUrlsUnset(string? urls, string? httpPorts, int expectedPort)
    {
        Assert.Equal($"http://localhost:{expectedPort}/health/ready",
            HealthCheckCliCommand.DefaultReadyUrl(urls, httpPorts));
    }
}
