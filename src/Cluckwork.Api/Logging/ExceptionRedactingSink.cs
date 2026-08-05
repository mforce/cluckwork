namespace Cluckwork.Api.Logging;

using Serilog.Core;
using Serilog.Events;

// The structural gap a property-mutating ILogEventEnricher cannot close, split
// out of #273's log-redaction work as its own reviewable unit (codex review
// round 2, P1b).
//
// An ILogEventEnricher only ever sees `logEvent.Properties`. `LogEvent.Exception`
// is a get-only property with no mutator and no Serilog API to replace it, and
// Serilog renders it SEPARATELY from the properties (the `{Exception}` output
// token calls `Exception.ToString()`; `Serilog.Formatting.Compact` writes the
// same text as `@x`). So an ordinary `logger.LogError(ex, ...)` sends the
// exception's message and stack text to every sink completely unredacted —
// including Npgsql's, whose messages routinely carry the connection string.
//
// The only place in Serilog's pipeline where an event can be REPLACED rather
// than merely mutated is a sink. This wrapper therefore sits between the logger
// and the real sinks: it rebuilds the LogEvent with a redacted stand-in
// exception and forwards. RedactingLoggerPipeline is what guarantees it wraps
// EVERY sink (config-declared and DI-registered alike) rather than one of them
// — see that class for the coverage argument, and
// docs/security/log-redaction-policy.md for what is and is not covered.
//
// The original event is forwarded untouched whenever redaction changed nothing,
// which is the overwhelmingly common case: an ordinary exception keeps its real
// CLR type, stack trace and inner-exception chain, and only an exception whose
// rendered text actually contains something sensitive is substituted.
//
// `redactText` is injected rather than calling SensitiveDataRedactionEnricher
// directly: this sink and RedactingLoggerPipeline are the generic "every sink
// sees a chance to rewrite the exception" MECHANISM, reviewable on its own
// terms (wiring, level semantics, sink coverage) independent of what any
// particular redaction function actually does. What runs through the delegate
// is the caller's decision — see CluckworkTelemetryServiceCollectionExtensions
// for the real one.
public sealed class ExceptionRedactingSink(ILogEventSink inner, Func<string, string> redactText) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent)
    {
        var replacement = RedactedException.For(logEvent.Exception, redactText);
        inner.Emit(ReferenceEquals(replacement, logEvent.Exception)
            ? logEvent
            : new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                replacement,
                logEvent.MessageTemplate,
                logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)),
                logEvent.TraceId ?? default,
                logEvent.SpanId ?? default));
    }

    // `inner` is the stage-two sub-logger `LoggerSinkConfiguration.Wrap` built
    // (a `SecondaryLoggerSink`, disposable). Serilog's root `AggregateSink` only
    // disposes sinks it directly holds — this wrapper, not what it wraps — so
    // without delegating here, stage two (and any buffered/disposable sink
    // inside it) never gets disposed and shutdown can drop unflushed events
    // (codex review of #426).
    public void Dispose() => (inner as IDisposable)?.Dispose();
}

// Stand-in for an exception whose rendered text carried something sensitive.
//
// A real exception's `StackTrace` is set by the runtime at throw time and its
// `Message` is fixed at construction, so a redacted copy cannot be produced by
// mutating the original — it has to be a different object. What matters is that
// every way a sink can render an exception yields the redacted text:
// `ToString()` (the `{Exception}` output token and Compact JSON's `@x`),
// `Message` (structured formatters that project it), and `StackTrace` (which is
// already contained in, and therefore only duplicated by, the rendered detail).
public sealed class RedactedException : Exception
{
    private readonly string detail;

    private RedactedException(string message, string detail)
        : base(message) => this.detail = detail;

    // Returns the ORIGINAL instance when redaction changed nothing, so callers
    // can use reference equality to decide whether the event needs rebuilding.
    public static Exception? For(Exception? exception, Func<string, string> redactText)
    {
        if (exception is null) return null;

        var rendered = exception.ToString();
        var redacted = redactText(rendered);
        return string.Equals(rendered, redacted, StringComparison.Ordinal)
            ? exception
            : new RedactedException(redactText(exception.Message), redacted);
    }

    public override string ToString() => detail;

    // The redacted stack text is already part of `detail`; exposing it a second
    // time here would only give a formatter a second, unredacted-looking place
    // to read from.
    public override string? StackTrace => null;
}
