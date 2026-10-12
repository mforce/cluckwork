using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Serilog.Core;
using Serilog.Events;
using static Cluckwork.Api.IntegrationTests.Mcp.McpConnection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #1227 — what a model and the log see when a tool call fails: a bad argument is named with
// its expected shape, a tool's own refusal logs below Error with no stack trace, and any
// other exception stays an Error whose text never reaches the model.
[Collection(IntegrationCollection.Name)]
public sealed class McpToolErrorTests(CluckworkWebApplicationFactory factory)
{
    private const string Entry =
        "\"idempotencyKey\":\"k\",\"flock\":\"Any\",\"date\":\"2026-10-11\",\"totalEggs\":1,"
        + "\"crackedEggs\":0,\"dirtyEggs\":0,\"discardedEggs\":0,\"mortalityCount\":0";

    // Each value fails the SDK's binding of its parameter's declared type.
    public static TheoryData<string, string, string, string> BadArguments => new()
    {
        { "cluckwork_list_daily_entries", OAuthScopes.ReadFarm, """{"from":"10/11/2026"}""", "from must be a date, YYYY-MM-DD." },
        { "cluckwork_list_daily_entries", OAuthScopes.ReadFarm, """{"from":123}""", "from must be a date, YYYY-MM-DD." },
        { "cluckwork_list_flocks", OAuthScopes.ReadFarm, """{"limit":"ten"}""", "limit must be a whole number." },
        { "cluckwork_list_flocks", OAuthScopes.ReadFarm, """{"limit":null}""", "limit must be a whole number." },
        { "cluckwork_list_flocks", OAuthScopes.ReadFarm, """{"limit":" 1"}""", "limit must be a whole number." },
        { "cluckwork_list_flocks", OAuthScopes.ReadFarm, """{"limit":2147483648}""", "limit must be a whole number from -2147483648 to 2147483647." },
        { "cluckwork_list_sales_orders", OAuthScopes.ReadFarm, """{"customerId":"Acme"}""", "customerId must be an id." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, $$"""{{{Entry}},"grades":"invalid"}""", "grades must be a list." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, $$"""{{{Entry}},"grades":[1]}""", "grades[0] must be an object." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, $$"""{{{Entry}},"grades":[{"grade":"Large","quantity":"ten"}]}""", "grades[0].quantity must be a whole number." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, $$"""{{{Entry}},"grades":[{"grade":"Large","quantity":null}]}""", "grades[0].quantity must be a whole number." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, $$"""{{{Entry}},"grades":[{"grade":"Large","Quantity":"ten"}]}""", "grades[0].quantity must be a whole number." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, $$"""{{{Entry.Replace("\"Any\"", "null")}}}""", "flock is required." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, $$"""{{{Entry.Replace("\"Any\"", "\" \"")}}}""", "flock is required." },
        { DailyEntryTools.RecordDailyEntry, OAuthScopes.WriteDailyEntries, """{"idempotencyKey":"k","flock":"Any","totalEggs":1,"crackedEggs":0,"dirtyEggs":0,"discardedEggs":0,"mortalityCount":0}""", "date is required." },
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task BadArgument_IsNamedWithItsShape_AndLogsNoError(string tool, string scope, string arguments, string expected)
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, scope);

        var text = await ErrorAsync(client, tool, Parse(arguments));

        Assert.Equal($"An error occurred invoking '{tool}': {expected}", text);
        AssertLoggedBelowError(sink, tool);
    }

    // The SDK reads integers from strings, and the check binds as the SDK does.
    [Fact]
    public async Task NumericString_StillBinds()
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.ReadFarm);

        var page = await PageAsync<JsonElement>(client, "cluckwork_list_flocks", Parse("""{"limit":"2"}"""));

        Assert.True(page.Complete);
    }

    [Fact]
    public async Task NotFoundRefusal_ReachesTheModel_AndLogsNoError()
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.ReadFarm);

        var text = await ErrorAsync(client, "cluckwork_list_daily_entries", new() { ["flock"] = "No Such Flock" });

        Assert.Equal("An error occurred invoking 'cluckwork_list_daily_entries': No flock you can see is named or numbered 'No Such Flock'.", text);
        AssertLoggedBelowError(sink, "cluckwork_list_daily_entries");
        Assert.DoesNotContain(sink.Observed, e => Render(e).Contains("No Such Flock"));
    }

    // Each argument here is valid, so a JsonException or ArgumentException from the body is
    // a fault, not a binding failure.
    [Theory]
    [InlineData(FaultTools.Unexpected)]
    [InlineData(FaultTools.Json)]
    [InlineData(FaultTools.Argument)]
    public async Task UnexpectedException_LogsAtError_AndLeaksNothing(string tool)
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.ReadFarm);

        var text = await ErrorAsync(client, tool, new() { ["date"] = "2026-10-11" });

        Assert.Equal($"An error occurred invoking '{tool}'.", text);
        var error = Assert.Single(sink.Observed, e => e.Level == LogEventLevel.Error);
        Assert.Contains(FaultTools.Secret, error.Exception?.Message);
    }

    // The SDK constructs the tool before it binds the arguments. A construction fault with
    // valid arguments is a fault; with a bad argument, the argument is named and the tool is
    // never constructed.
    [Fact]
    public async Task ConstructionFault_LogsAtError_UnlessAnArgumentIsBad()
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.ReadFarm);

        var fault = await ErrorAsync(client, ConstructionFaultTools.Name, Parse("""{"date":"2026-10-11"}"""));
        var error = Assert.Single(sink.Observed, e => e.Level == LogEventLevel.Error);
        sink.Clear();
        var bad = await ErrorAsync(client, ConstructionFaultTools.Name, Parse("""{"date":"10/11/2026"}"""));

        Assert.Equal($"An error occurred invoking '{ConstructionFaultTools.Name}'.", fault);
        Assert.Contains(FaultTools.Secret, error.Exception?.ToString());
        Assert.Equal($"An error occurred invoking '{ConstructionFaultTools.Name}': date must be a date, YYYY-MM-DD.", bad);
        AssertLoggedBelowError(sink, ConstructionFaultTools.Name);
    }

    // A tool's protocol error stays a JSON-RPC error, as the SDK sends it.
    [Fact]
    public async Task ProtocolError_StaysAJsonRpcError()
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.ReadFarm);

        var refused = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync(FaultTools.Protocol, new Dictionary<string, object?> { ["date"] = "2026-10-11" }).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, refused.ErrorCode);
    }

    private static void AssertLoggedBelowError(Sink sink, string tool)
    {
        Assert.DoesNotContain(sink.Observed, e => e.Level >= LogEventLevel.Error);
        var refusal = Assert.Single(sink.Observed, e => e.MessageTemplate.Text.StartsWith("MCP tool {Tool} refused"));
        Assert.Equal(LogEventLevel.Information, refusal.Level);
        Assert.Null(refusal.Exception);
        Assert.Equal($"\"{tool}\"", refusal.Properties["Tool"].ToString());
    }

    private WebApplicationFactory<Program> Host(Sink sink) =>
        McpConnection.Host(factory).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(sink);
            services.AddMcpServer().WithTools<FaultTools>().WithTools<ConstructionFaultTools>();
        }));

    // Only the call under test is left in the sink.
    private async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> host, Sink sink, string scope)
    {
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [scope]);
        var client = await McpConnection.ConnectAsync(host, token);
        sink.Clear();
        return client;
    }

    private static Dictionary<string, object?> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(a => a.Key, a => (object?)a.Value);

    private static string Render(LogEvent e) =>
        string.Join('\n', [e.RenderMessage(), .. e.Properties.Values.Select(v => v.ToString()), e.Exception?.ToString() ?? ""]);

    private sealed class Sink : ILogEventSink
    {
        private readonly HashSet<ActivityTraceId> cleared = [];

        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);

        // A request already logged can still finish after the clear, the handshake's or an
        // earlier call's, and a cancelled one logs a 500 at Error (#1246). Late events of
        // those requests are dropped by trace; an event of unknown origin stays.
        public void Clear()
        {
            foreach (var e in Events)
                if (e.TraceId is { } trace)
                    cleared.Add(trace);
            Events.Clear();
        }

        public IEnumerable<LogEvent> Observed => Events.Where(e => e.TraceId is not { } trace || !cleared.Contains(trace));
    }

    [McpServerToolType]
    public sealed class FaultTools
    {
        public const string Unexpected = "cw1227_unexpected";
        public const string Json = "cw1227_json";
        public const string Argument = "cw1227_argument";
        public const string Protocol = "cw1227_protocol";
        public const string Secret = "internal detail cw1227";

        [McpServerTool(Name = Unexpected), Description("Throws.")]
        public string ThrowUnexpected(DateOnly date) => throw new InvalidOperationException(Secret);

        [McpServerTool(Name = Json), Description("Throws.")]
        public string ThrowJson(DateOnly date) => throw new JsonException(Secret);

        [McpServerTool(Name = Argument), Description("Throws.")]
        public string ThrowArgument(DateOnly date) => throw new ArgumentException(Secret);

        [McpServerTool(Name = Protocol), Description("Throws.")]
        public string ThrowProtocol(DateOnly date) => throw new McpProtocolException(Secret, McpErrorCode.InvalidParams);
    }

    [McpServerToolType]
    public sealed class ConstructionFaultTools
    {
        public const string Name = "cw1227_construction";

        public ConstructionFaultTools() => throw new JsonException(FaultTools.Secret);

        [McpServerTool(Name = Name), Description("Cannot be constructed.")]
        public string Read(DateOnly date) => date.ToString();
    }
}
