namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

// #214 — every request emits one structured completion log (method, path,
// status, elapsed) carrying the request's TraceId, so a single id joins the
// request line, handler logs, and exported spans. Health probes are demoted
// below Information so liveness polling doesn't flood the log.
public sealed class RequestLoggingFactory : CluckworkWebApplicationFactory
{
    public CollectingSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // Program.cs pulls DI-registered sinks into the logger via
            // ReadFrom.Services; this hands the test a live tap on every event.
            services.AddSingleton<ILogEventSink>(Sink);
            // Outside Development, minimal-API binding failures return 400
            // without throwing; the exception-path test needs the real throw.
            services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
                o => o.ThrowOnBadRequest = true);
        });
    }

    public sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

[Collection(RequestLoggingCollection.Name)]
public sealed class RequestLoggingTests(RequestLoggingFactory factory)
{
    private const string RequestLoggerContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private IReadOnlyList<LogEvent> CompletionEventsFor(string path) =>
        [.. factory.Sink.Events.Where(e =>
            ScalarOf(e, "SourceContext") == RequestLoggerContext
            && ScalarOf(e, "RequestPath") == path)];

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    [Fact]
    public async Task Request_emits_one_completion_log_with_method_path_status_elapsed_and_traceid()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/flocks");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        var completion = Assert.Single(CompletionEventsFor("/api/v1/flocks"));
        Assert.Equal("GET", ScalarOf(completion, "RequestMethod"));
        Assert.Equal("401", ScalarOf(completion, "StatusCode"));
        Assert.True(completion.Properties.ContainsKey("Elapsed"), "completion log must carry Elapsed");
        Assert.NotNull(completion.TraceId);
        Assert.NotEqual(default, completion.TraceId!.Value);
    }

    [Fact]
    public async Task Incoming_traceparent_becomes_the_logged_traceid()
    {
        var client = factory.CreateClient();
        var traceId = ActivityTraceId.CreateRandom();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/egg-grades");
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        await client.SendAsync(request);

        var completion = Assert.Single(CompletionEventsFor("/api/v1/egg-grades"));
        Assert.Equal(traceId, completion.TraceId);
    }

    [Fact]
    public async Task Health_probes_do_not_emit_information_level_request_logs()
    {
        var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        live.EnsureSuccessStatusCode();
        ready.EnsureSuccessStatusCode();
        Assert.Empty(CompletionEventsFor("/health/live"));
        Assert.Empty(CompletionEventsFor("/health/ready"));
    }

    // A FAILING probe must stay demoted too — during a DB outage orchestrators
    // poll /health/ready every few seconds; 503s must not flood Error (codex
    // review of PR #226).
    [Fact]
    public async Task Failing_readiness_probe_is_not_logged_at_error()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default",
                "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1;Command Timeout=1");
            builder.UseSetting("Database:MigrateOnStartup", "false");
        });

        var response = await broken.CreateClient().GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(CompletionEventsFor("/health/ready"));
    }

    // An unhandled exception re-executes the pipeline at /error; without
    // demotion that produced TWO completion lines per failed request (codex +
    // agent review of PR #226). The malformed-JSON body is the one guaranteed
    // in-repo trigger: minimal-API binding throws BadHttpRequestException.
    [Fact]
    public async Task Unhandled_exception_logs_one_completion_for_the_real_path_only()
    {
        var client = factory.CreateClient();
        var malformed = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/auth/login", malformed);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        // The exception-pass completion (Serilog logs these with StatusCode 500
        // + the exception attached) — filtered by status so the healthy logins
        // other tests perform can't interfere.
        Assert.Single(CompletionEventsFor("/api/v1/auth/login"),
            e => ScalarOf(e, "StatusCode") == "500");
        Assert.Empty(CompletionEventsFor("/error"));
    }

    // Spec §10: account_id on every log scope. The tenant middleware feeds the
    // resolved account into the request completion via IDiagnosticContext.
    [Fact]
    public async Task Authenticated_request_completion_carries_the_account_id()
    {
        var email = $"reqlog-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var token = await factory.LoginForAccessTokenAsync(email);
        var client = factory.CreateAuthedClient(token);

        var response = await client.GetAsync("/api/v1/customers");

        response.EnsureSuccessStatusCode();
        var completion = Assert.Single(CompletionEventsFor("/api/v1/customers"));
        Assert.Equal(accountId.ToString(), ScalarOf(completion, "AccountId"));
    }
}

// Own collection (not "integration"): this class uses its own factory/container
// so its log tap only sees this class's traffic.
[CollectionDefinition(Name)]
public sealed class RequestLoggingCollection : ICollectionFixture<RequestLoggingFactory>
{
    public const string Name = "request-logging";
}
