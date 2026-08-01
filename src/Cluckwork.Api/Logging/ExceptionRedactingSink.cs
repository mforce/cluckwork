namespace Cluckwork.Api.Logging;

using Serilog.Core;
using Serilog.Events;

// #273 codex review (round 2, P1b) — the structural gap
// SensitiveDataRedactionEnricher cannot close.
//
// An ILogEventEnricher only ever sees `logEvent.Properties`. `LogEvent.Exception`
// is a get-only property with no mutator and no Serilog API to replace it, and
// Serilog renders it SEPARATELY from the properties (the `{Exception}` output
// token calls `Exception.ToString()`; `Serilog.Formatting.Compact` writes the
// same text as `@x`). So every `logger.LogError(ex, ...)` sent the exception's
// message and stack text to the sinks completely unredacted — including
// Npgsql's, whose messages routinely carry the connection string, and including
// the Auth.RefreshRevocationFailed event this PR itself added.
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
public sealed class ExceptionRedactingSink(ILogEventSink inner) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var replacement = RedactedException.For(logEvent.Exception);
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
    public static Exception? For(Exception? exception)
    {
        if (exception is null) return null;

        var rendered = exception.ToString();
        var redacted = SensitiveDataRedactionEnricher.RedactText(rendered);
        return string.Equals(rendered, redacted, StringComparison.Ordinal)
            ? exception
            : new RedactedException(
                SensitiveDataRedactionEnricher.RedactText(exception.Message), redacted);
    }

    public override string ToString() => detail;

    // The redacted stack text is already part of `detail`; exposing it a second
    // time here would only give a formatter a second, unredacted-looking place
    // to read from.
    public override string? StackTrace => null;
}
