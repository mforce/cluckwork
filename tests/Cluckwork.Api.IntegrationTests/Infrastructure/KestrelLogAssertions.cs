namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using Serilog.Events;

// Shared by every Kestrel-backed test class that needs to prove a request
// completed cleanly server-side even when the client-visible status code
// can't show it (#340: the response can commit before a later throw). See
// KestrelBackedFactory's header comment for why this observation point
// (the Serilog completion line, not the HTTP status) is the one that matters.
internal static class KestrelLogAssertions
{
    private const string RequestLoggerContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    // The sink is shared by the whole fixture, and a test may hit the same
    // path more than once (a create + its replay). Take this mark BEFORE the
    // requests under assertion so their completion lines can be told apart
    // from every earlier request's — including earlier tests'. ConcurrentQueue
    // enumerates in enqueue order, so a count is a usable cursor.
    public static int MarkLog(this KestrelBackedFactory factory) => factory.Sink.Events.Count;

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    // The response can be committed before the server-side throw, so the
    // client's response can arrive while the server is still unwinding. Wait
    // for the completion lines rather than racing them — one is emitted per
    // request, on both the clean and the throwing path.
    public static async Task<IReadOnlyList<LogEvent>> CompletionsForAsync(
        this KestrelBackedFactory factory, string path, int mark, int expected)
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
    // since the mark carries no exception and did not escalate to Error.
    //
    // `expected` is asserted rather than inferred: checking only the FIRST
    // match would let a regression confined to a later request on the same
    // path pass on the strength of an earlier request's clean line (Codex,
    // #341 round 1).
    public static async Task AssertServerSideCleanAsync(
        this KestrelBackedFactory factory, string path, int mark, int expected)
    {
        var completions = await factory.CompletionsForAsync(path, mark, expected);
        Assert.Equal(expected, completions.Count);
        foreach (var completion in completions)
        {
            Assert.Null(completion.Exception);
            Assert.True(completion.Level < LogEventLevel.Error,
                $"{path} completed at {completion.Level}: {completion.Exception}");
        }
    }
}
