namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

internal static class CluckworkTelemetryServiceCollectionExtensions
{
    public static CluckworkTelemetryRegistration AddCluckworkTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ReadFrom.Services lets DI-registered enrichers/sinks join the pipeline —
        // the integration tests tap the logger this way (#214).
        // preserveStaticLogger: the MEL bridge must bind THIS host's logger, never
        // the process-global Log.Logger — a co-hosted Serilog app's shutdown flips
        // the static to SilentLogger and every logger category created afterwards
        // goes permanently quiet (third bug of this family: options.Logger, then
        // DiagnosticContext, now the bridge itself).
        services.AddSerilog((registeredServices, cfg) => cfg
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(registeredServices), preserveStaticLogger: true);

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
        var traceEndpoint = otlp.Enabled ? otlp.ResolveTraceEndpoint() : null;
        var metricsEndpoint = otlp.Enabled ? otlp.ResolveMetricsEndpoint() : null;

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
