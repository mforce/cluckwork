namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;

// #404 — Production emits compact JSON to stdout so a log collector can index
// structured fields instead of grepping prose. Development keeps the human
// template.
//
// Field names are NOT the same across the two, which is a trap for anyone
// writing a collector query: ambient Activity context reaches Serilog as the
// LogEvent.TraceId/SpanId fields, which CompactJsonFormatter writes as
// `@tr`/`@sp`, while Development's outputTemplate renders the same values under
// `{TraceId}`/`{SpanId}`. Ordinary properties (AccountId, StatusCode) keep
// their names in both. See The_bound_production_formatter_emits_trace_context_as_at_tr.
//
// The invariant these tests exist to protect is NOT "a formatter is configured"
// but "the base layer contributes no outputTemplate for Production to collide
// with". Verified against Serilog.Settings.Configuration 10.0.0: when a layered
// appsettings merge leaves BOTH `outputTemplate` and `formatter` on one Console
// sink, the binder silently selects the outputTemplate overload and IGNORES the
// formatter. No exception, no dropped sink — Production just keeps logging
// prose while the config file claims otherwise. That is why
// Production_console_sink_contributes_no_output_template below is load-bearing,
// why the base appsettings.json Console entry carries no Args at all, and why
// Production_configuration_binds_the_compact_json_formatter drives the binder
// rather than trusting the config leaves to imply its result.
public sealed class ProductionLogFormatTests
{
    private const string ConsoleSink = "Serilog:WriteTo:console";
    private const string CompactFormatter =
        "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact";
    private const string CompactFormatterTypeName =
        "Serilog.Formatting.Compact.CompactJsonFormatter";

    // The sink graph is a handful of wrappers deep; this only bounds the walk
    // against a cycle the visited-set somehow misses.
    private const int MaxSinkGraphDepth = 8;

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
    //
    // The Name assertion is not decoration: counting alone would stay green if
    // an environment file replaced the console entry with some other sink, so
    // the count and the identity have to be pinned together.
    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Exactly_one_console_sink_is_configured(string? environment)
    {
        var config = LoadMergedConfiguration(environment);

        var sink = Assert.Single(config.GetSection("Serilog:WriteTo").GetChildren());
        Assert.Equal("Console", sink["Name"]);
    }

    // Guards the assembly-qualified type name in appsettings.Production.json: a
    // typo there makes this unresolvable long before anyone notices Production
    // logs look wrong. Resolved from the value the config file actually
    // carries, not from this file's constant, so the test cannot agree with
    // itself while disagreeing with Production.
    //
    // It does NOT guard the direct PackageReference, and must not be read as
    // doing so — Serilog.Formatting.Compact was already transitive via
    // Serilog.AspNetCore, so deleting the direct reference leaves the assembly
    // in the graph and every test here green. The reference is a deliberate,
    // untested choice: it states the dependency this config file relies on.
    [Fact]
    public void The_named_formatter_type_resolves()
    {
        var configured = LoadMergedConfiguration("Production")[$"{ConsoleSink}:Args:formatter"];

        Assert.NotNull(configured);
        Assert.NotNull(Type.GetType(configured, throwOnError: false));
    }

    // THE tests, and the ones the assertions above cannot stand in for.
    // Everything else here reads config leaves or drives a formatter directly;
    // the behaviour this change exists to control — Serilog.Settings.Configuration
    // choosing between the outputTemplate and formatter overloads — happens
    // inside ReadFrom.Configuration, which none of them execute. A binder
    // regression (a Serilog upgrade changing overload selection, an argument
    // name drifting) would leave the real Console sink on prose while every
    // other test in this file stayed green.
    //
    // Deliberately NOT done by capturing Console.Out. Console.SetOut is
    // process-global, xunit parallelises test classes by default (there is no
    // xunit.runner.json here), and the CluckworkWebApplicationFactory classes
    // log continuously from their own ConsoleSink instances — which hold a
    // DIFFERENT _syncRoot, so nothing serialises their writes against the
    // probe's. The interleaving is therefore SUB-LINE: a foreign event can
    // splice bytes into the middle of the probe's own line, which no
    // line-level marker filter can undo. Reaching the bound formatter by
    // reflection asks the same question deterministically. Version-brittle by
    // construction, but it fails loudly and in CI rather than one run in fifty.
    [Fact]
    public void Production_configuration_binds_the_compact_json_formatter()
    {
        Assert.Equal(
            [CompactFormatterTypeName],
            BoundFormatterNames("Production"));
    }

    // The other side of the same binder. Without it, moving the human template
    // into the base file would satisfy every Production assertion here and
    // quietly change what a developer sees on every `dotnet run`.
    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    public void Non_production_configurations_bind_a_template_renderer(string? environment)
    {
        Assert.DoesNotContain(CompactFormatterTypeName, BoundFormatterNames(environment));
    }

    // The correlation guarantee, pinned against a REAL trace-carrying event
    // rather than a property this fixture named "TraceId" itself. Those are
    // different fields and only one of them is what a request produces:
    // Serilog surfaces ambient Activity context through LogEvent.TraceId, and
    // CompactJsonFormatter writes it as `@tr`/`@sp` — NOT as `TraceId`, which
    // is what Development's outputTemplate renders it under. A collector query
    // written against the wrong one silently matches nothing, so the name is
    // asserted here and stated in AGENTS.md.
    [Fact]
    public void The_bound_production_formatter_emits_trace_context_as_at_tr()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var formatter = Assert.IsType<ITextFormatter>(BoundFormatters("Production").Single(), exactMatch: false);
        using var output = new StringWriter();

        formatter.Format(SampleEvent(LogEventLevel.Information, traceId, spanId), output);

        using var parsed = JsonDocument.Parse(output.ToString());
        Assert.Equal(traceId.ToHexString(), parsed.RootElement.GetProperty("@tr").GetString());
        Assert.Equal(spanId.ToHexString(), parsed.RootElement.GetProperty("@sp").GetString());
    }

    private static string[] BoundFormatterNames(string? environment) =>
        [.. BoundFormatters(environment).Select(f => f.GetType().FullName!).Distinct()];

    // Builds the logger the way Program.cs does (ReadFrom.Configuration over
    // the merged environment config), then walks the constructed sink graph for
    // whatever ITextFormatter the binder actually wired in. The walk is generic
    // rather than reaching for a named private field, so a Serilog refactor
    // that renames internals does not silently stop finding anything — an empty
    // result fails the assertions above.
    private static ITextFormatter[] BoundFormatters(string? environment)
    {
        using var logger = new LoggerConfiguration()
            .ReadFrom.Configuration(LoadMergedConfiguration(environment))
            .CreateLogger();

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var found = new List<ITextFormatter>();
        Walk(logger, 0);
        return [.. found];

        void Walk(object? node, int depth)
        {
            if (node is null || depth > MaxSinkGraphDepth || !seen.Add(node))
                return;

            if (node is ITextFormatter formatter)
                found.Add(formatter);

            if (node is System.Collections.IEnumerable sequence and not string)
                foreach (var item in sequence)
                    Walk(item, depth + 1);

            foreach (var field in node.GetType().GetFields(
                         BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!field.FieldType.IsPrimitive && field.FieldType != typeof(string))
                    Walk(field.GetValue(node), depth + 1);
            }
        }
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
        Assert.Equal("marker-abc", parsed.RootElement.GetProperty("Marker").GetString());
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

    private static LogEvent SampleEvent(
        LogEventLevel level,
        ActivityTraceId? traceId = null,
        ActivitySpanId? spanId = null) => new(
        DateTimeOffset.UnixEpoch,
        level,
        exception: null,
        new MessageTemplateParser().Parse("probe {Marker}"),
        [new LogEventProperty("Marker", new ScalarValue("marker-abc"))],
        traceId ?? default,
        spanId ?? default);

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
