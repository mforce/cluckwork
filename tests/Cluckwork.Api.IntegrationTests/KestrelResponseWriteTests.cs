namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

// #340 — every write endpoint returning 204 threw under a real server:
//
//   System.InvalidOperationException: Writing to the response body is invalid
//   for responses with status code 204.
//      at ...Kestrel.Core.Internal.Http.HttpProtocol.Advance(Int32 bytes)
//      at ...HttpResponseWritingExtensions.WriteAsync(...)
//      at Cluckwork.Api.Middleware.IdempotencyMiddleware.InvokeAsync(...)
//
// IdempotencyMiddleware buffers the handler's response and then echoes it back
// onto the restored body stream. #307 swapped the echo from
// `buffer.CopyToAsync(originalBody)` — which performs ZERO writes on an empty
// buffer — to `Response.WriteAsync(body)`, which still calls
// `pipeWriter.GetSpan(...)` + `Advance(0)` for an empty string. RFC 9110
// forbids a body on 204/205/304 and Kestrel enforces it on ANY write,
// including that zero-length one.
//
// TWO independent gaps let this ship, and the test below has to close both.
//
// 1. TRANSPORT. The rest of the suite runs on the in-memory TestServer, whose
//    response writer does not enforce the no-body rule at all. So this class
//    runs the app over a real socket: UseKestrel(0)
//    (Microsoft.AspNetCore.Mvc.Testing 10) binds 127.0.0.1 on a free port and
//    rewrites the factory's client base address to it.
//
// 2. OBSERVATION POINT. The status code alone CANNOT see this bug. WriteAsync
//    calls StartAsync first, so the 204 response line and headers are already
//    on the wire by the time Advance throws — the client receives a complete,
//    valid 204 and ExceptionHandlerMiddleware can no longer touch it ("the
//    response has already started"). Asserting `Assert.Equal(NoContent, …)`
//    passes with the bug fully present. The only client-visible trace is
//    server-side: the request's Serilog completion line carries the exception
//    and escalates to Error. That is what these tests assert on.
public sealed class KestrelBackedFactory : CluckworkWebApplicationFactory
{
    // Must run before anything touches Services (which starts the host). The
    // base constructor doesn't, and InitializeAsync — which does — runs later.
    public KestrelBackedFactory() => UseKestrel(0);

    public CollectingSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Program.cs pulls DI-registered sinks into the logger via
        // ReadFrom.Services (#214) — a live tap on every event this host emits.
        builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(Sink));
    }

    public sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

public sealed class KestrelResponseWriteTests(KestrelBackedFactory factory)
    : IClassFixture<KestrelBackedFactory>
{
    private const string RequestLoggerContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private sealed record IdDto(Guid Id);

    private async Task<HttpClient> SetupClientAsync()
    {
        var email = $"kestrel-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
    }

    private static async Task<Guid> CreateGradeAsync(HttpClient client)
    {
        var create = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = $"K-{Guid.NewGuid():N}"[..12], gradeType = "custom", sortOrder = 1, isSaleable = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    // The sink is shared by the whole fixture, and several requests in these
    // tests hit the SAME path (the first PUT and its replay; the create POST
    // and its replay). Take this mark BEFORE the requests under assertion so
    // the completion lines that belong to them can be told apart from every
    // earlier request's — including earlier tests'. ConcurrentQueue enumerates
    // in enqueue order, so a count is a usable cursor.
    private int MarkLog() => factory.Sink.Events.Count;

    // The response is committed before the throw, so the client's 204 arrives
    // while the server is still unwinding. Wait for the completion lines rather
    // than racing them — one is emitted per request, on both the clean and the
    // throwing path.
    private async Task<IReadOnlyList<LogEvent>> CompletionsForAsync(string path, int mark, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var matches = factory.Sink.Events.Skip(mark).Where(e =>
                ScalarOf(e, "SourceContext") == RequestLoggerContext
                && ScalarOf(e, "RequestPath") == path).ToList();
            if (matches.Count >= expected) return matches;
            Assert.True(DateTimeOffset.UtcNow < deadline,
                $"Expected {expected} request-completion logs for {path}, saw {matches.Count}");
            await Task.Delay(50);
        }
    }

    // Asserts the server handled the requests cleanly: EVERY completion line
    // since the mark carries no exception and did not escalate to Error. With
    // the #340 bug present the line reads
    //   Error … InvalidOperationException: Writing to the response body is
    //   invalid for responses with status code 204
    // while the client still sees its 204.
    //
    // `expected` is asserted rather than inferred: checking only the FIRST
    // match would let a regression confined to ReplayAsync pass on the strength
    // of the preceding publish request's clean line (Codex, #341 round 1).
    private async Task AssertServerSideCleanAsync(string path, int mark, int expected)
    {
        var completions = await CompletionsForAsync(path, mark, expected);
        Assert.Equal(expected, completions.Count);
        foreach (var completion in completions)
        {
            Assert.Null(completion.Exception);
            Assert.True(completion.Level < LogEventLevel.Error,
                $"{path} completed at {completion.Level}: {completion.Exception}");
        }
    }

    // The regression itself.
    [Fact]
    public async Task NoContentWrite_OverKestrel_CompletesWithoutWritingToTheBody()
    {
        var client = await SetupClientAsync();
        var id = await CreateGradeAsync(client);
        var path = $"/api/v1/egg-grades/{id}";
        var mark = MarkLog();

        var update = await client.PutWithKeyAsync(
            path, Guid.NewGuid().ToString(),
            new { name = "Renamed", sortOrder = 7, isSaleable = false });

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(string.Empty, await update.Content.ReadAsStringAsync());
        await AssertServerSideCleanAsync(path, mark, expected: 1);
    }

    // The REPLAY path (IdempotencyMiddleware.ReplayAsync) writes the cached
    // body on its own code path, so it needs its own guard: a client retry
    // after a dropped connection must not throw either.
    [Fact]
    public async Task NoContentWrite_ReplayedOverKestrel_CompletesWithoutWritingToTheBody()
    {
        var client = await SetupClientAsync();
        var id = await CreateGradeAsync(client);
        var path = $"/api/v1/egg-grades/{id}";
        var key = Guid.NewGuid().ToString();
        var body = new { name = "Replayed", sortOrder = 3, isSaleable = true };
        var mark = MarkLog();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutWithKeyAsync(path, key, body)).StatusCode);

        // Same key, same payload — served from the cached record, not re-executed.
        var replay = await client.PutWithKeyAsync(path, key, body);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.Equal(string.Empty, await replay.Content.ReadAsStringAsync());
        // Both the publish and the replay line, so a ReplayAsync-only
        // regression cannot hide behind the publish request's clean line.
        await AssertServerSideCleanAsync(path, mark, expected: 2);
    }

    // Counterweight: a fix that simply stopped echoing the buffered body would
    // pass both tests above. A 2xx that DOES carry a body must still deliver
    // it, on the first pass and on replay.
    [Fact]
    public async Task BodiedWrite_OverKestrel_StillDeliversBodyOnFirstPassAndReplay()
    {
        var client = await SetupClientAsync();
        var key = Guid.NewGuid().ToString();
        var payload = new { name = $"B-{Guid.NewGuid():N}"[..12], gradeType = "custom", sortOrder = 2, isSaleable = true };
        // /api/v1/egg-grades is hit by every test's CreateGradeAsync, so the
        // mark is what makes these two completion lines this test's own.
        var mark = MarkLog();

        var create = await client.PostWithKeyAsync("/api/v1/egg-grades", key, payload);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<IdDto>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);

        var replay = await client.PostWithKeyAsync("/api/v1/egg-grades", key, payload);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(created.Id, (await replay.Content.ReadFromJsonAsync<IdDto>())!.Id);
        await AssertServerSideCleanAsync("/api/v1/egg-grades", mark, expected: 2);
    }
}
