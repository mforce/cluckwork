namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;
using Cluckwork.Api.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

internal static class CluckworkTelemetryServiceCollectionExtensions
{
    // isServingProcess is false for the one-off CLI verbs (migrate / seed /
    // recover-admin). They dispatch after Build(), so a throw during service
    // registration would abort them before CliDispatcher ever ran — see the
    // endpoint-resolution block below for why that matters.
    public static CluckworkTelemetryRegistration AddCluckworkTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool isServingProcess = true)
    {
        // ReadFrom.Services lets DI-registered enrichers/sinks join the pipeline —
        // the integration tests tap the logger this way (#214).
        // preserveStaticLogger: the MEL bridge must bind THIS host's logger, never
        // the process-global Log.Logger — a co-hosted Serilog app's shutdown flips
        // the static to SilentLogger and every logger category created afterwards
        // goes permanently quiet (third bug of this family: options.Logger, then
        // DiagnosticContext, now the bridge itself).
        // #273 — redact credentials/tokens/cookies/connection strings/emails
        // BEFORE any sink sees the event, covering both the event's PROPERTIES
        // (an enricher) and its EXCEPTION (a sink wrapper — an enricher
        // structurally cannot reach LogEvent.Exception). RedactingLoggerPipeline
        // owns the whole construction, including `ReadFrom.Configuration` /
        // `ReadFrom.Services`, because the guarantee it provides is that every
        // sink from either source sits behind the wrapper; read its class
        // comment before changing how the logger is built here.
        services.AddSerilog(
            (registeredServices, cfg) =>
                RedactingLoggerPipeline.Configure(cfg, configuration, registeredServices),
            preserveStaticLogger: true);

        // Bind IDiagnosticContext property creation to THIS host's logger. The
        // default falls back to the process-global static Log.Logger at Set() time.
        services.AddSingleton(sp =>
            new Serilog.Extensions.Hosting.DiagnosticContext(
                sp.GetRequiredService<Serilog.ILogger>()));
        services.AddSingleton<Serilog.IDiagnosticContext>(sp =>
            sp.GetRequiredService<Serilog.Extensions.Hosting.DiagnosticContext>());

        // OTLP export is config-gated (#214). Endpoint and protocol are validated
        // eagerly so configuration errors fail at boot.
        var otlp = configuration.GetSection(OtlpOptions.SectionName).Get<OtlpOptions>()
            ?? new OtlpOptions();
        var protocol = otlp.ParseProtocol();
        var isProduction = environment.IsProduction();
        Uri? traceEndpoint = null;
        Uri? metricsEndpoint = null;
        if (otlp.Enabled)
        {
            try
            {
                traceEndpoint = otlp.ResolveTraceEndpoint(isProduction);
                metricsEndpoint = otlp.ResolveMetricsEndpoint(isProduction);
            }
            catch (InvalidOperationException ex) when (!isServingProcess)
            {
                // A one-off verb must not die on a telemetry misconfiguration it
                // does not depend on. `recover-admin` in particular is the
                // break-glass path for a locked-out farm (#265) and is
                // deliberately NOT environment-gated — an unrelated bad
                // Otlp:Endpoint blocking an emergency password reset would be a
                // worse failure than the one this validation prevents. Degrade to
                // export DISABLED rather than exporting insecurely: the verb runs,
                // and nothing leaves the process over an unvalidated endpoint.
                Console.Error.WriteLine(
                    $"warning: OTLP export disabled for this command — {ex.Message}");
            }
        }

        Action<OtlpExporterOptions> ConfigureOtlpExporter(Uri endpoint) => options =>
        {
            options.Endpoint = endpoint;
            options.Protocol = protocol;
            if (!string.IsNullOrWhiteSpace(otlp.Headers))
                options.Headers = otlp.Headers;
        };

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Cluckwork.Api"))
            .WithTracing(trace =>
            {
                trace
                    // Do not let an internet client suppress server tracing with
                    // a traceparent sampled=0 flag.
                    .SetSampler(new AlwaysOnSampler())
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health"))
                    .AddEntityFrameworkCoreInstrumentation();
                if (traceEndpoint is not null)
                    trace.AddOtlpExporter(ConfigureOtlpExporter(traceEndpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Npgsql", "Microsoft.EntityFrameworkCore");
                if (metricsEndpoint is not null)
                    metrics.AddOtlpExporter(ConfigureOtlpExporter(metricsEndpoint));
            });

        return new CluckworkTelemetryRegistration(traceEndpoint, metricsEndpoint, protocol);
    }
}

internal sealed record CluckworkTelemetryRegistration(
    Uri? TraceEndpoint,
    Uri? MetricsEndpoint,
    OtlpExportProtocol Protocol);
