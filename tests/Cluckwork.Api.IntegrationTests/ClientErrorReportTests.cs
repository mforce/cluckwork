namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

// #217 — the SPA's ErrorBoundary reports render crashes to the API, which
// writes them to the server log at Error level with structured fields. The
// endpoint is anonymous (the login screen can crash too), size-capped and
// rate-limited per IP so it cannot be used as a log-flooding vector, and
// stores nothing.
public sealed class ClientErrorReportFactory : CluckworkWebApplicationFactory
{
    public const int Limit = 3;

    public RequestLoggingFactory.CollectingSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:ClientErrors:PermitLimit", Limit.ToString());
        builder.UseSetting("RateLimiting:ClientErrors:WindowSeconds", "900");
        builder.ConfigureTestServices(services =>
        {
            // Program.cs pulls DI-registered sinks into the logger via
            // ReadFrom.Services; this hands the test a live tap on every event.
            services.AddSingleton<Serilog.Core.ILogEventSink>(Sink);
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
        });
    }
}

public sealed class ClientErrorReportTests(ClientErrorReportFactory factory)
    : IClassFixture<ClientErrorReportFactory>
{
    private const string Path = "/api/v1/client-errors";

    // Distinct socket IP per test: the per-IP limiter must not let one test's
    // reports eat another's budget.
    private HttpClient ClientFrom(string ip)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Remote", ip);
        return client;
    }

    private IReadOnlyList<LogEvent> ReportEvents() =>
        [.. factory.Sink.Events.Where(e =>
            ScalarOf(e, "SourceContext")?.Contains("ClientError") == true)];

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    private static object ValidReport(string message = "boom") => new
    {
        message,
        stack = "Error: boom\n    at Crash (src/routes/DashboardPage.tsx:12:3)",
        componentStack = "\n    at Crash\n    at ErrorBoundary",
        scope = "screen",
        route = "/daily-entries",
        appVersion = "1.2.3",
        traceId = "0123456789abcdef0123456789abcdef"
    };

    [Fact]
    public async Task Valid_report_returns_202_and_logs_one_error_event_with_structured_fields()
    {
        var client = ClientFrom("203.0.113.101");
        var marker = $"crash-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(Path, ValidReport(marker));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var logged = Assert.Single(ReportEvents(),
            e => ScalarOf(e, "Message")?.Contains(marker) == true);
        Assert.Equal(LogEventLevel.Error, logged.Level);
        Assert.Equal("screen", ScalarOf(logged, "Scope"));
        Assert.Equal("/daily-entries", ScalarOf(logged, "Route"));
        Assert.Contains("DashboardPage.tsx", ScalarOf(logged, "Stack"));
        Assert.Contains("ErrorBoundary", ScalarOf(logged, "ComponentStack"));
        Assert.Equal("1.2.3", ScalarOf(logged, "AppVersion"));
        Assert.Equal("0123456789abcdef0123456789abcdef", ScalarOf(logged, "ClientTraceId"));
    }

    [Fact]
    public async Task App_scope_is_accepted_and_logged()
    {
        var client = ClientFrom("203.0.113.102");
        var marker = $"crash-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(Path, new
        {
            message = marker,
            scope = "app"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var logged = Assert.Single(ReportEvents(),
            e => ScalarOf(e, "Message")?.Contains(marker) == true);
        Assert.Equal("app", ScalarOf(logged, "Scope"));
    }

    [Fact]
    public async Task Oversized_report_is_rejected_with_413_and_not_logged()
    {
        var client = ClientFrom("203.0.113.103");
        var marker = $"crash-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(Path, new
        {
            message = marker,
            stack = new string('x', 64 * 1024),
            scope = "screen"
        });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain(ReportEvents(),
            e => ScalarOf(e, "Message")?.Contains(marker) == true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_message_is_rejected_with_400(string message)
    {
        var client = ClientFrom("203.0.113.104");

        var response = await client.PostAsJsonAsync(Path, new { message, scope = "screen" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_scope_is_rejected_with_400()
    {
        var client = ClientFrom("203.0.113.105");

        var response = await client.PostAsJsonAsync(Path,
            new { message = "boom", scope = "galaxy" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reports_are_rate_limited_per_ip()
    {
        var throttled = ClientFrom("203.0.113.106");

        for (var i = 0; i < ClientErrorReportFactory.Limit; i++)
        {
            var ok = await throttled.PostAsJsonAsync(Path, ValidReport());
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var limited = await throttled.PostAsJsonAsync(Path, ValidReport());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"),
            "429 must carry a Retry-After header");

        // A different client IP still has its own budget.
        var other = ClientFrom("203.0.113.107");
        var fresh = await other.PostAsJsonAsync(Path, ValidReport());
        Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
    }

    [Fact]
    public async Task Overlong_fields_are_truncated_in_the_log_not_rejected()
    {
        // Under the byte cap but over the per-field bound: the report is
        // accepted and the logged property is cut, so a single pathological
        // stack can't balloon one log line.
        var client = ClientFrom("203.0.113.108");
        var marker = $"crash-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(Path, new
        {
            message = marker + new string('m', 4000),
            stack = new string('s', 10_000),
            scope = "screen"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var logged = Assert.Single(ReportEvents(),
            e => ScalarOf(e, "Message")?.Contains(marker) == true);
        Assert.True(ScalarOf(logged, "Message")!.Length <= 2000);
        Assert.True(ScalarOf(logged, "Stack")!.Length <= 8000);
    }
}
