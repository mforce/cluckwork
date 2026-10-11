using System.Collections.Concurrent;
using System.ComponentModel;
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
    [Theory]
    [InlineData("cluckwork_list_daily_entries", "from", "10/11/2026", "from must be a date, YYYY-MM-DD.")]
    [InlineData("cluckwork_list_flocks", "limit", "ten", "limit must be a whole number.")]
    [InlineData("cluckwork_list_sales_orders", "customerId", "Acme", "customerId must be an id.")]
    public async Task BadArgument_IsNamedWithItsShape_AndLogsNoError(string tool, string argument, string value, string expected)
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.ReadFarm);

        var text = await ErrorAsync(client, tool, new() { [argument] = value });

        Assert.Equal($"An error occurred invoking '{tool}': {expected}", text);
        AssertLoggedBelowError(sink, tool);
    }

    [Fact]
    public async Task BadNestedArgument_IsNamedByItsPath()
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.WriteDailyEntries);

        var text = await ErrorAsync(client, DailyEntryTools.RecordDailyEntry, new()
        {
            ["idempotencyKey"] = "k", ["flock"] = "Any", ["date"] = "2026-10-11", ["totalEggs"] = 1,
            ["crackedEggs"] = 0, ["dirtyEggs"] = 0, ["discardedEggs"] = 0, ["mortalityCount"] = 0,
            ["grades"] = new[] { new { grade = "Large", quantity = "ten" } },
        });

        Assert.EndsWith(": grades[0].quantity must be a whole number.", text);
        AssertLoggedBelowError(sink, DailyEntryTools.RecordDailyEntry);
    }

    [Fact]
    public async Task MissingRequiredArgument_IsNamed()
    {
        var sink = new Sink();
        using var host = Host(sink);
        await using var client = await ConnectAsync(host, sink, OAuthScopes.WriteDailyEntries);

        var text = await ErrorAsync(client, DailyEntryTools.RecordDailyEntry, new()
        {
            ["idempotencyKey"] = "k", ["flock"] = "Any", ["totalEggs"] = 1,
            ["crackedEggs"] = 0, ["dirtyEggs"] = 0, ["discardedEggs"] = 0, ["mortalityCount"] = 0,
        });

        Assert.EndsWith(": date is required.", text);
        AssertLoggedBelowError(sink, DailyEntryTools.RecordDailyEntry);
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
        Assert.DoesNotContain(sink.Events, e => Render(e).Contains("No Such Flock"));
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
        var error = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Error);
        Assert.Contains(FaultTools.Secret, error.Exception?.Message);
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
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Error);
        var refusal = Assert.Single(sink.Events, e => e.MessageTemplate.Text.StartsWith("MCP tool {Tool} refused"));
        Assert.Equal(LogEventLevel.Information, refusal.Level);
        Assert.Null(refusal.Exception);
        Assert.Equal($"\"{tool}\"", refusal.Properties["Tool"].ToString());
    }

    private WebApplicationFactory<Program> Host(Sink sink) =>
        McpConnection.Host(factory).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(sink);
            services.AddMcpServer().WithTools<FaultTools>();
        }));

    // Only the call under test is left in the sink.
    private async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> host, Sink sink, string scope)
    {
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), [scope]);
        var client = await McpConnection.ConnectAsync(host, token);
        sink.Events.Clear();
        return client;
    }

    private static string Render(LogEvent e) =>
        string.Join('\n', [e.RenderMessage(), .. e.Properties.Values.Select(v => v.ToString()), e.Exception?.ToString() ?? ""]);

    private sealed class Sink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
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
}
