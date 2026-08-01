namespace Cluckwork.Api.IntegrationTests;

using System.Collections.Concurrent;
using Cluckwork.Api.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// #273 codex review (round 2, P1b) — RedactingLoggerPipeline, exercised as the
// real host builds it: a sink declared in `Serilog:WriteTo` (the app's own sink
// contract — appsettings.json declares Console exactly this way) and a sink
// registered in DI (`ReadFrom.Services`, the #214 tap) must BOTH sit behind
// ExceptionRedactingSink, and the level configuration must survive the
// restructure that put them there. A unit test on the redaction helper alone
// would pass while every one of those was broken.
public sealed class RedactingLoggerPipelineTests
{
    // Thrown and caught, never constructed: the text a sink receives is
    // Exception.ToString(), which only carries a real stack trace once the
    // runtime has actually thrown it.
    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private sealed class DiSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private static (ILogger Logger, ConcurrentQueue<LogEvent> Config, ConcurrentQueue<LogEvent> Di)
        BuildHostLikePipeline(params (string Key, string Value)[] settings)
    {
        ConfigDeclaredCollectingSink.Events.Clear();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Exactly the shape appsettings.json uses for Console: a sink
                // named in Serilog:WriteTo, constructed by
                // Serilog.Settings.Configuration, never by this test's code.
                ["Serilog:WriteTo:0:Name"] = "Sink",
                ["Serilog:WriteTo:0:Args:sink"] =
                    "Cluckwork.Api.IntegrationTests.ConfigDeclaredCollectingSink, Cluckwork.Api.IntegrationTests",
                ["Serilog:Enrich:0"] = "FromLogContext",
            }.Concat(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))))
            .Build();

        var di = new DiSink();
        var services = new ServiceCollection()
            .AddSingleton<ILogEventSink>(di)
            .BuildServiceProvider();

        var loggerConfiguration = new LoggerConfiguration();
        RedactingLoggerPipeline.Configure(loggerConfiguration, configuration, services);
        return (loggerConfiguration.CreateLogger(), ConfigDeclaredCollectingSink.Events, di.Events);
    }

    [Fact]
    public void An_exception_message_carrying_a_credential_is_redacted_before_a_CONFIG_declared_sink_sees_it()
    {
        var (logger, configSink, _) = BuildHostLikePipeline();
        var secret = Guid.NewGuid().ToString("N");
        // Thrown and caught so the exception carries a REAL stack trace, the way
        // a live failure would — the rendered text a sink receives is
        // ToString(), not just Message.
        var thrown = Thrown($"connect failed: Host=db;Password=\"{secret}\";");

        logger.Error(thrown, "Revocation failed");
        (logger as IDisposable)?.Dispose();

        var e = Assert.Single(configSink);
        Assert.NotNull(e.Exception);
        Assert.DoesNotContain(secret, e.Exception!.ToString());
        Assert.DoesNotContain(secret, e.Exception!.Message);
        Assert.Contains("[REDACTED]", e.Exception!.ToString());
        // Redaction, not deletion: the diagnostic value is preserved.
        Assert.Contains("connect failed", e.Exception!.ToString());
        Assert.Contains("InvalidOperationException", e.Exception!.ToString());
    }

    [Fact]
    public void An_exception_message_carrying_a_credential_is_redacted_before_a_DI_registered_sink_sees_it()
    {
        var (logger, _, diSink) = BuildHostLikePipeline();
        var secret = Guid.NewGuid().ToString("N");
        var thrown = Thrown($"connect failed: Host=db;Password=\"{secret}\";");

        logger.Error(thrown, "Revocation failed");
        (logger as IDisposable)?.Dispose();

        var e = Assert.Single(diSink);
        Assert.NotNull(e.Exception);
        Assert.DoesNotContain(secret, e.Exception!.ToString());
        Assert.Contains("[REDACTED]", e.Exception!.ToString());
    }

    [Fact]
    public void An_exception_with_nothing_sensitive_reaches_the_sink_completely_unaltered()
    {
        var (logger, configSink, _) = BuildHostLikePipeline();
        var thrown = Thrown("ordinary failure");

        logger.Error(thrown, "Something broke");
        (logger as IDisposable)?.Dispose();

        var e = Assert.Single(configSink);
        // Same instance — real CLR type, real stack, real inner-exception chain.
        Assert.Same(thrown, e.Exception);
    }

    // The restructure that put every sink behind the wrapper moved sink
    // configuration into a sub-logger. If the level settings had moved with it,
    // `Serilog:MinimumLevel` would have become a no-op on the logger callers
    // actually hold — every EF Core Debug event materialised in Production.
    [Fact]
    public void Minimum_level_from_configuration_still_governs_the_logger_callers_hold()
    {
        var (logger, configSink, diSink) = BuildHostLikePipeline(
            ("Serilog:MinimumLevel:Default", "Warning"));

        Assert.False(logger.IsEnabled(LogEventLevel.Information));
        logger.Information("dropped");
        logger.Warning("kept");
        (logger as IDisposable)?.Dispose();

        Assert.Single(configSink);
        Assert.Single(diSink);
        Assert.Equal("kept", configSink.Single().RenderMessage());
    }

    // The other half, and the one a sub-logger silently breaks: a sub-logger
    // re-checks a forwarded event against its own FLAT minimum level and never
    // re-applies the per-source override map, so an event a `MinimumLevel:
    // Override` deliberately enabled would be dropped on the way to the sinks
    // unless stage 2 is explicitly permissive.
    [Fact]
    public void A_per_source_minimum_level_override_still_reaches_the_sinks()
    {
        var (logger, configSink, _) = BuildHostLikePipeline(
            ("Serilog:MinimumLevel:Default", "Warning"),
            ("Serilog:MinimumLevel:Override:Chatty", "Debug"));

        logger.ForContext(Constants.SourceContextPropertyName, "Chatty.Component").Debug("override-enabled");
        logger.ForContext(Constants.SourceContextPropertyName, "Quiet.Component").Debug("override-absent");
        (logger as IDisposable)?.Dispose();

        var e = Assert.Single(configSink);
        Assert.Equal("override-enabled", e.RenderMessage());
    }

    [Fact]
    public void Property_redaction_still_applies_on_the_way_to_every_sink()
    {
        var (logger, configSink, diSink) = BuildHostLikePipeline();
        var secret = $"secret-{Guid.NewGuid():N}";

        logger.Warning("Login attempt with {Password}", secret);
        (logger as IDisposable)?.Dispose();

        foreach (var events in new[] { configSink, diSink })
        {
            var e = Assert.Single(events);
            Assert.DoesNotContain(secret, e.RenderMessage());
            Assert.Contains("[REDACTED]", e.RenderMessage());
        }
    }
}

// Instantiated by Serilog.Settings.Configuration from the `Serilog:WriteTo`
// entry above, so its events are collected statically — the test never gets to
// hand the pipeline a sink instance, which is the point: this proves a sink the
// APP's configuration declares is wrapped, not one the test wired up itself.
public sealed class ConfigDeclaredCollectingSink : ILogEventSink
{
    public static ConcurrentQueue<LogEvent> Events { get; } = new();

    public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
}
