namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Users.ChangeOwnPassword;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

// #398 review (Codex) — a genuine unhandled fault for
// Unhandled_exception_logs_one_completion_for_the_real_path_only below. That
// test used to provoke malformed JSON at /api/v1/auth/login, but that request
// no longer reaches this middleware as an exception at all: BindingFailureResponse
// (Hosting/BindingFailureResponse.cs) catches it before Serilog ever sees it
// (see Binding_failure_logs_one_completion_at_information_with_status_400
// instead). This class needs its own genuine, non-binding fault to keep
// proving the Error/500 guarantee. Mirrors ExceptionHandlerReExecutionTests.cs's
// validator-replacement pattern: the throw originates INSIDE the endpoint —
// downstream of IdempotencyMiddleware, exactly where a real handler fault
// would — rather than from binding.
internal sealed class ThrowingChangeOwnPasswordValidatorForRequestLogging
    : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ThrowingChangeOwnPasswordValidatorForRequestLogging() =>
        RuleFor(c => c.NewPassword).Custom((_, _) =>
            throw new InvalidOperationException("unhandled-exception probe"));
}

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
            // #398 — RouteHandlerOptions.ThrowOnBadRequest=true (so a binding
            // failure really throws, which the binding-failure test below
            // needs) is now Program.cs's own global registration, not a
            // per-factory override — every environment behaves the same way.
            services.AddScoped<IValidator<ChangeOwnPasswordCommand>, ThrowingChangeOwnPasswordValidatorForRequestLogging>();
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
    // agent review of PR #226). Malformed JSON used to be the guaranteed
    // in-repo trigger (minimal-API binding throws BadHttpRequestException),
    // but #398 review (Codex) gave THAT specific exception its own middleware
    // (BindingFailureResponse) that catches it before Serilog ever sees it —
    // see Binding_failure_logs_one_completion_at_information_with_status_400
    // for that case. This test now needs a genuine, non-binding fault instead
    // (ThrowingChangeOwnPasswordValidatorForRequestLogging, registered above)
    // to keep proving the guarantee for a REAL unhandled exception.
    [Fact]
    public async Task Unhandled_exception_logs_one_completion_for_the_real_path_only()
    {
        var email = $"reqlog-fault-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = TestHarness.Password,
            newPassword = $"{Guid.NewGuid():N}aA1!"
        });

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        // The exception-pass completion (Serilog logs these with StatusCode 500
        // + the exception attached) — filtered by status so other tests'
        // traffic on this path can't interfere.
        var completion = Assert.Single(CompletionEventsFor("/api/v1/auth/change-password"),
            e => ScalarOf(e, "StatusCode") == "500");
        Assert.Equal(LogEventLevel.Error, completion.Level);
        Assert.Empty(CompletionEventsFor("/error"));
    }

    // #398 review (Codex) — the regression this branch fixes, and the
    // client-visible half of that same fix, pinned together from ONE request.
    // Filtered by StatusCode, like Unhandled_exception_logs_... above: other
    // tests' factory.LoginForAccessTokenAsync setup calls ALSO hit
    // /api/v1/auth/login on the same shared, never-reset factory.Sink — those
    // always succeed (200), so filtering this request's own 400 out keeps
    // this test's assertion independent of what else in the class ran first.
    //
    // The regression: a JSON-binding failure (RouteHandlerOptions.
    // ThrowOnBadRequest, forced true in every environment) is a
    // BadHttpRequestException that, unhandled, propagates through
    // UseSerilogRequestLogging on its way to being correctly mapped to a 400
    // at /error. Serilog.AspNetCore hardcodes StatusCode 500 at Error for ANY
    // exception that passes through it, so the client's correct 400 was
    // being mis-logged as a server fault — inflating 5xx/error telemetry and
    // risking false alerts. BindingFailureResponse
    // (Hosting/BindingFailureResponse.cs) now answers the request itself,
    // one layer inside Serilog, so Serilog only ever sees a normal 400
    // completion — and moving WHERE the response is written must not change
    // WHAT is written, so this also pins the exact ValidationProblem shape
    // ValidationResponse.BindingFailureProblem() produces (the single
    // factory both BindingFailureResponse and the /error backstop in
    // Program.cs call, so they cannot drift apart).
    [Fact]
    public async Task Binding_failure_logs_one_completion_at_information_with_status_400()
    {
        var client = factory.CreateClient();
        var malformed = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/auth/login", malformed);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        // (1) Exactly one completion for THIS request, at Information,
        // StatusCode 400 — not 500, not Error — and no exception-handler
        // re-execution at /error (the failure never propagated that far up
        // the pipeline).
        var completion = Assert.Single(CompletionEventsFor("/api/v1/auth/login"),
            e => ScalarOf(e, "StatusCode") == "400");
        Assert.Equal(LogEventLevel.Information, completion.Level);
        Assert.Empty(CompletionEventsFor("/error"));

        // (3) The client-visible body is still the ValidationProblem shape
        // #398 introduced, not a bare 400 and not the framework's raw
        // binding-exception text.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Failed to read parameter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Utf8JsonReader", body, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        var bodyErrors = doc.RootElement.GetProperty("errors").GetProperty("body");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, bodyErrors.ValueKind);
        Assert.Equal(
            "The request body has an invalid or incorrectly formatted value.",
            bodyErrors[0].GetString());
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

    // #398 review round 2 (Codex) — the STREAMED 413, which is a different path
    // from the 400 above and was not covered by anything.
    //
    // RequestBodyLimit's recovery for this case is a post-`next()` STATUS CHECK,
    // not a catch: it relies on minimal-API's generated binder catching
    // ByteCappedRequestStream's 413 itself, setting Response.StatusCode = 413,
    // and returning WITHOUT rethrowing. Forcing ThrowOnBadRequest=true globally
    // (#398) is exactly the kind of change that could invalidate that premise —
    // if the binder rethrew instead, `next()` would throw, the recovery would
    // never run, BindingFailureResponse would rethrow it (413 != 400), and
    // Serilog would log the hardcoded 500/Error this PR exists to eliminate.
    //
    // AuthBodyLimitTests pins the RESPONSE shape for this path and still passes,
    // but a response assertion cannot see a mis-logged completion — the original
    // #398 defect was invisible to every response-level test in the suite. So
    // assert the telemetry directly: one completion, Information, 413.
    [Fact]
    public async Task Streamed_413_logs_one_completion_at_information_with_status_413()
    {
        var client = factory.CreateClient();

        // Declares 10 bytes, actually sends ~8 KB: the cap is breached mid-read,
        // inside the generated binder, rather than by the declared-length
        // short-circuit that never reaches binding at all.
        //
        // NonSeekableStream, shared with AuthBodyLimitTests, NOT a MemoryStream
        // (#398 review round 2, Codex): both paths answer 413, so a test that
        // drifted onto the declared-length short-circuit would still pass while
        // proving nothing about the streamed one. Matching the transport shape
        // AuthBodyLimitTests already uses for this path keeps that honest by
        // construction rather than by argument.
        var payload = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"email\":\"nobody@example.com\",\"password\":\"{new string('a', 8192)}\"}}");
        var content = new StreamContent(new NonSeekableStream(payload));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 10;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = content });

        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // Filter by status rather than Assert.Single on the path: the sink is
        // class-scoped and never reset, and several other tests here reach
        // /api/v1/auth/login through the login helper, so a bare Single() fails
        // on their traffic rather than on this guarantee. Exactly one 413
        // completion is still the assertion — a re-executed /error would add a
        // second, and a mis-logged 500 would leave zero.
        var completion = Assert.Single(
            CompletionEventsFor("/api/v1/auth/login"),
            e => ScalarOf(e, "StatusCode") == "413");
        Assert.Equal(LogEventLevel.Information, completion.Level);
        Assert.Null(completion.Exception);
    }
}

// Own collection (not "integration"): this class uses its own factory/container
// so its log tap only sees this class's traffic.
[CollectionDefinition(Name)]
public sealed class RequestLoggingCollection : ICollectionFixture<RequestLoggingFactory>
{
    public const string Name = "request-logging";
}
