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
    public async Task Rendered_fields_are_stripped_of_control_characters_against_log_forging()
    {
        // A plain-text sink renders {Message}/{Route} into a line; CR/LF (or
        // ANSI escapes) from this ANONYMOUS source would let one report forge
        // additional log lines. Stacks stay verbatim, since a stack without
        // newlines is useless.
        //
        // Post-#404 that plain-text sink is Development's; Production formats
        // as compact JSON, where the writer escapes control characters and no
        // value can forge a record. The stripping is unconditional anyway — the
        // sink format is a configuration choice this endpoint cannot see — so
        // this test holds for both.
        var client = ClientFrom("203.0.113.110");
        var marker = $"crash-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(Path, new
        {
            message = $"{marker}\r\n[00:00:00 INF] forged line \x1b[31m",
            stack = "line one\nline two",
            scope = "screen",
            route = "/daily\nentries"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var logged = Assert.Single(ReportEvents(),
            e => ScalarOf(e, "Message")?.Contains(marker) == true);
        Assert.DoesNotContain('\n', ScalarOf(logged, "Message")!);
        Assert.DoesNotContain('\r', ScalarOf(logged, "Message")!);
        Assert.DoesNotContain('\x1b', ScalarOf(logged, "Message")!);
        Assert.DoesNotContain('\n', ScalarOf(logged, "Route")!);
        Assert.Contains("line one\nline two", ScalarOf(logged, "Stack"));
    }

    // #273 — the concrete leak the issue was filed against: this endpoint's
    // whole point is writing CALLER-CONTROLLED free text to the log
    // (Message/Stack), and until now nothing scrubbed it. Values are
    // generated at runtime, never hardcoded (a literal secret in a test file
    // is exactly what GitGuardian flags, hardcoded or not).
    [Fact]
    public async Task Report_content_containing_an_email_and_connection_credentials_is_redacted_before_it_reaches_the_log()
    {
        var client = ClientFrom("203.0.113.120");
        var marker = $"crash-{Guid.NewGuid():N}";
        var fakeEmail = $"{Guid.NewGuid():N}@example.test";
        var fakeSecret = Guid.NewGuid().ToString("N");

        var response = await client.PostAsJsonAsync(Path, new
        {
            message = $"{marker} reported by {fakeEmail} conn=\"Host=db;Password={fakeSecret};\"",
            stack = $"Error handling request for {fakeEmail}",
            scope = "screen"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var logged = Assert.Single(ReportEvents(),
            e => ScalarOf(e, "Message")?.Contains(marker) == true);

        var loggedMessage = ScalarOf(logged, "Message")!;
        var loggedStack = ScalarOf(logged, "Stack")!;
        Assert.DoesNotContain(fakeEmail, loggedMessage);
        Assert.DoesNotContain(fakeSecret, loggedMessage);
        Assert.DoesNotContain(fakeEmail, loggedStack);
        Assert.Contains("[REDACTED]", loggedMessage);
        Assert.Contains("[REDACTED]", loggedStack);
        // The non-PII marker survives — this is REDACTION, not truncation.
        Assert.Contains(marker, loggedMessage);
    }

    [Fact]
    public async Task Missing_message_field_is_rejected_with_400()
    {
        var client = ClientFrom("203.0.113.111");
        var response = await client.PostAsJsonAsync(Path, new { scope = "screen" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Non_json_body_is_rejected_with_400()
    {
        var client = ClientFrom("203.0.113.112");
        var response = await client.PostAsync(Path,
            new StringContent("not json at all", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_exactly_at_the_cap_is_accepted_and_one_byte_over_is_rejected()
    {
        // Probe both sides of the boundary: a report padded to EXACTLY
        // MaxReportBytes must pass; one more byte must 413.
        var client = ClientFrom("203.0.113.113");
        var marker = $"crash-{Guid.NewGuid():N}";

        static string PaddedTo(string marker, int targetBytes)
        {
            var skeleton = $$"""{"message":"{{marker}}","scope":"screen","stack":""}""";
            return $$"""{"message":"{{marker}}","scope":"screen","stack":"{{new string('s', targetBytes - System.Text.Encoding.UTF8.GetByteCount(skeleton))}}"}""";
        }

        var atCap = PaddedTo(marker, Cluckwork.Api.Endpoints.ClientErrors.ClientErrorEndpoints.MaxReportBytes);
        Assert.Equal(Cluckwork.Api.Endpoints.ClientErrors.ClientErrorEndpoints.MaxReportBytes,
            System.Text.Encoding.UTF8.GetByteCount(atCap));
        var accepted = await client.PostAsync(Path,
            new StringContent(atCap, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var overCap = PaddedTo(marker, Cluckwork.Api.Endpoints.ClientErrors.ClientErrorEndpoints.MaxReportBytes + 1);
        var rejected = await client.PostAsync(Path,
            new StringContent(overCap, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
    }

    [Fact]
    public async Task Chunked_body_with_no_declared_length_is_still_capped()
    {
        // A hostile client can omit Content-Length entirely (chunked); the
        // declared-length early exit never fires and the capped read loop is
        // the only guard. This is the path that test must pin.
        var client = ClientFrom("203.0.113.114");
        var marker = $"crash-{Guid.NewGuid():N}";

        var oversized = $$"""{"message":"{{marker}}","scope":"screen","stack":"{{new string('s', 64 * 1024)}}"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new ChunkedContent(System.Text.Encoding.UTF8.GetBytes(oversized))
        };
        request.Headers.TransferEncodingChunked = true;

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain(ReportEvents(),
            e => ScalarOf(e, "Message")?.Contains(marker) == true);
    }

    // StringContent always computes a Content-Length; this content refuses to
    // declare one, forcing the chunked/no-length path through the endpoint.
    private sealed class ChunkedContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
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
