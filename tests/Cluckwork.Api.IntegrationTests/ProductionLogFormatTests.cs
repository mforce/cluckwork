namespace Cluckwork.Api.IntegrationTests;

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;

// #404 — Production emits compact JSON to stdout so a log collector can index
// TraceId/AccountId/status instead of grepping prose. Development keeps the
// human template.
//
// The invariant these tests exist to protect is NOT "a formatter is configured"
// but "the base layer contributes no outputTemplate for Production to collide
// with". Verified against Serilog.Settings.Configuration 10.0.0: when a layered
// appsettings merge leaves BOTH `outputTemplate` and `formatter` on one Console
// sink, the binder silently selects the outputTemplate overload and IGNORES the
// formatter. No exception, no dropped sink — Production just keeps logging
// prose while the config file claims otherwise. That is why
// Production_console_sink_contributes_no_output_template below is the
// load-bearing test, and why the base appsettings.json Console entry carries no
// Args at all.
//
// Deliberately NOT asserted by capturing Console.Out: the sink writes to
// process-global stdout, and this suite runs test classes in parallel with
// others that boot real hosts and log continuously, so a captured buffer would
// be polluted by unrelated output. These assertions cover the two things a
// regression would actually break — what the merged config says, and what the
// named formatter emits.
public sealed class ProductionLogFormatTests
{
    private const string ConsoleSink = "Serilog:WriteTo:console";
    private const string CompactFormatter =
        "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact";

    [Fact]
    public void Production_console_sink_selects_the_compact_json_formatter()
    {
        var config = LoadMergedConfiguration("Production");

        Assert.Equal(CompactFormatter, config[$"{ConsoleSink}:Args:formatter"]);
    }

    // The load-bearing one. A base-layer outputTemplate would merge in beside
    // the formatter and silently win, leaving Production on prose.
    [Fact]
    public void Production_console_sink_contributes_no_output_template()
    {
        var config = LoadMergedConfiguration("Production");

        Assert.Null(config[$"{ConsoleSink}:Args:outputTemplate"]);
    }

    // The other side of the boundary: proves the base layer really is empty
    // rather than the Production file merely happening to win. If someone moves
    // the human template back into appsettings.json, this stays green while the
    // test above goes red — so the pair localises the mistake.
    [Fact]
    public void Base_configuration_alone_contributes_no_output_template()
    {
        var config = LoadMergedConfiguration(environment: null);

        Assert.Null(config[$"{ConsoleSink}:Args:outputTemplate"]);
        Assert.Null(config[$"{ConsoleSink}:Args:formatter"]);
    }

    // An unknown/absent environment must still reach a sink. Without this, a
    // "tidy-up" that moved the whole WriteTo block into the two environment
    // files would leave any other environment logging nowhere at all.
    [Fact]
    public void Base_configuration_alone_still_configures_the_console_sink()
    {
        var config = LoadMergedConfiguration(environment: null);

        Assert.Equal("Console", config[$"{ConsoleSink}:Name"]);
    }

    [Fact]
    public void Development_keeps_the_human_template_and_no_formatter()
    {
        var config = LoadMergedConfiguration("Development");

        var template = config[$"{ConsoleSink}:Args:outputTemplate"];
        Assert.NotNull(template);
        Assert.Contains("{TraceId}", template, StringComparison.Ordinal);
        Assert.Null(config[$"{ConsoleSink}:Args:formatter"]);
    }

    // A second sink entry under a different key would not collide — it would
    // duplicate, logging every event twice. Cheap to assert, and the shape is
    // an easy mistake when adding the OTLP log sink later.
    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Exactly_one_sink_is_configured(string? environment)
    {
        var config = LoadMergedConfiguration(environment);

        var sinks = config.GetSection("Serilog:WriteTo").GetChildren().ToArray();
        Assert.Single(sinks);
    }

    // Guards the assembly-qualified type name in appsettings.Production.json —
    // a typo, or losing the direct PackageReference, makes this unresolvable
    // long before anyone notices Production logs look wrong.
    [Fact]
    public void The_named_formatter_type_resolves()
    {
        Assert.NotNull(Type.GetType(CompactFormatter, throwOnError: false));
    }

    [Fact]
    public void The_named_formatter_emits_one_parseable_json_object_per_event()
    {
        var formatter = CreateConfiguredFormatter();
        using var output = new StringWriter();

        formatter.Format(SampleEvent(LogEventLevel.Information), output);

        var line = Assert.Single(
            output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var parsed = JsonDocument.Parse(line);

        Assert.True(parsed.RootElement.TryGetProperty("@t", out _));
        Assert.True(parsed.RootElement.TryGetProperty("@mt", out _));
        Assert.Equal("trace-abc", parsed.RootElement.GetProperty("TraceId").GetString());
    }

    // Compact JSON omits @l for Information (it is the default), so the level
    // has to be probed at a level that actually carries it — otherwise a
    // fixture asserting "@l is present" would fail against a correct formatter,
    // and one asserting "@l is absent" would pass against a broken one.
    [Fact]
    public void The_named_formatter_records_non_default_levels()
    {
        var formatter = CreateConfiguredFormatter();
        using var output = new StringWriter();

        formatter.Format(SampleEvent(LogEventLevel.Warning), output);

        using var parsed = JsonDocument.Parse(output.ToString());
        Assert.Equal("Warning", parsed.RootElement.GetProperty("@l").GetString());
    }

    // Resolves the formatter through the *configured* type name rather than
    // referencing CompactJsonFormatter directly, so the assertions above test
    // what Production actually names, not what this test file imports.
    //
    // OptionalParamBinding is required: the formatter's only constructor takes
    // an optional IFormatProvider, which Activator's default-constructor path
    // does not consider parameterless. Serilog's own settings binder fills
    // optional constructor arguments the same way.
    private static ITextFormatter CreateConfiguredFormatter()
    {
        var type = Type.GetType(CompactFormatter, throwOnError: true)!;
        return (ITextFormatter)Activator.CreateInstance(
            type,
            BindingFlags.CreateInstance | BindingFlags.Public | BindingFlags.Instance
                | BindingFlags.OptionalParamBinding,
            binder: null,
            args: [Type.Missing],
            culture: null)!;
    }

    private static LogEvent SampleEvent(LogEventLevel level) => new(
        DateTimeOffset.UnixEpoch,
        level,
        exception: null,
        new MessageTemplateParser().Parse("probe {TraceId}"),
        [new LogEventProperty("TraceId", new ScalarValue("trace-abc"))]);

    // Mirrors what WebApplicationBuilder does: the base file, then the
    // environment overlay. Reads the real shipped files so a config regression
    // cannot hide behind a fixture's own copy.
    private static IConfigurationRoot LoadMergedConfiguration(string? environment)
    {
        var apiProject = Path.Combine(
            FindRepositoryRoot().FullName, "src", "Cluckwork.Api");

        var builder = new ConfigurationBuilder()
            .SetBasePath(apiProject)
            .AddJsonFile("appsettings.json", optional: false);

        if (environment is not null)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: false);

        return builder.Build();
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.sln")))
                return directory;
        }

        throw new DirectoryNotFoundException("Could not locate the Cluckwork repository root.");
    }
}
