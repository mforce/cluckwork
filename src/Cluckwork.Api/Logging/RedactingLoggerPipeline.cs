namespace Cluckwork.Api.Logging;

using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

// #273 codex review (round 2, P1b) — builds this host's Serilog pipeline so
// that NO sink can be reached except through ExceptionRedactingSink.
//
// Why two stages rather than "add another enricher". Property redaction can be
// an enricher (SensitiveDataRedactionEnricher mutates `logEvent.Properties`
// in place), but `LogEvent.Exception` is get-only and Serilog offers no way for
// an enricher or an ILogEventFilter to SUBSTITUTE an event. The only pipeline
// element that receives an event and decides what the next element sees is a
// sink. So the exception redactor has to be a sink wrapper — and a wrapper is
// only a security control if it is impossible to register a sink beside it.
//
// Hence:
//
//   stage 1 (the logger callers hold)
//     · minimum levels + per-source overrides + destructuring, read from the
//       app's own `Serilog:` settings. These are the settings that must be
//       evaluated where the event is CREATED: `ILogger.IsEnabled` and the
//       `MinimumLevel.Override` map are consulted before an event exists at
//       all, and destructuring policies run in the message-template processor
//       of the logger the caller is holding. Keeping them here is what stops
//       this restructure from turning `Serilog:MinimumLevel` into a no-op and
//       materialising every EF Core Debug event in Production.
//     · exactly ONE sink: ExceptionRedactingSink wrapping stage 2.
//
//   stage 2 (a sub-logger, reachable only through that wrapper)
//     · every sink, from BOTH sources: `Serilog:WriteTo` in configuration and
//       `ILogEventSink` registrations in DI (`ReadFrom.Services`, the #214 tap).
//     · the configured enrichers (`Serilog:Enrich`, e.g. FromLogContext) and
//       then SensitiveDataRedactionEnricher, in that order — the property
//       redactor must run AFTER FromLogContext so that log-context properties
//       are redacted too, which is only true if it is appended last.
//     · `MinimumLevel.Verbose()`, applied after the configuration is read: a
//       sub-logger re-checks a forwarded event against its own flat minimum
//       level and, unlike stage 1, does NOT re-apply the per-source override
//       map — so anything but "pass everything stage 1 already allowed" would
//       silently drop events that an override had deliberately enabled.
//
// The consequence worth stating plainly: an operator who adds a sink via
// `Serilog:WriteTo`, and a test or future component that registers an
// `ILogEventSink` in DI, both land inside the wrapper automatically. There is
// no supported way to attach a sink to stage 1.
public static class RedactingLoggerPipeline
{
    // The `Serilog:` settings that must be applied to stage 1 because they are
    // consulted at event-CREATION time. Everything not listed here (WriteTo,
    // AuditTo, Enrich, Filter, Properties) is applied to stage 2 instead —
    // deliberately, so that no `WriteTo` entry can ever be attached to the
    // outer logger. `Using` rides along because `Destructure` entries may name
    // a type from an assembly it lists.
    private static readonly string[] EventCreationSettings =
        ["MinimumLevel", "LevelSwitches", "Destructure", "Using"];

    public static LoggerConfiguration Configure(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IServiceProvider services)
    {
        loggerConfiguration.ReadFrom.Configuration(EventCreationSettingsOf(configuration));

        // LoggerSinkConfiguration.Wrap builds (and owns the disposal of) the
        // wrapped sink chain; WriteTo.Sink then attaches the wrapper — and only
        // the wrapper — as stage 1's single sink.
        var redactedSinks = LoggerSinkConfiguration.Wrap(
            inner => new ExceptionRedactingSink(inner),
            sinks => sinks.Logger(stageTwo => stageTwo
                .ReadFrom.Configuration(configuration)
                .ReadFrom.Services(services)
                .MinimumLevel.Verbose()
                .Enrich.With(new SensitiveDataRedactionEnricher())));

        return loggerConfiguration.WriteTo.Sink(redactedSinks, LevelAlias.Minimum);
    }

    // A `Serilog:` settings view holding only the event-creation-time keys
    // above. Built by re-keying the app's own configuration entries rather than
    // by re-implementing Serilog.Settings.Configuration's parsing, so the
    // meaning of `MinimumLevel` / `Destructure` stays whatever the library says
    // it is, from whichever provider (file, env var, test `UseSetting`) supplied
    // it.
    private static IConfiguration EventCreationSettingsOf(IConfiguration configuration) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                configuration.GetSection("Serilog")
                    .AsEnumerable(makePathsRelative: true)
                    .Where(entry => entry.Value is not null
                        && EventCreationSettings.Any(setting =>
                            entry.Key.StartsWith(setting, StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => new KeyValuePair<string, string?>($"Serilog:{entry.Key}", entry.Value)))
            .Build();
}
