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
        // Program.cs pulls DI-registered sinks into the logger via
        // ReadFrom.Services; this hands the test a live tap on every event.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogEventSink>(Sink));
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
}

// Own collection (not "integration"): this class uses its own factory/container
// so its log tap only sees this class's traffic.
[CollectionDefinition(Name)]
public sealed class RequestLoggingCollection : ICollectionFixture<RequestLoggingFactory>
{
    public const string Name = "request-logging";
}
