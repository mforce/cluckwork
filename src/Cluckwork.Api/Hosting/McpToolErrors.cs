using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Hosting;

// #1227 — the SDK (2.2.0, McpServerImpl) logs every exception a tool call throws at Error
// with its stack trace, and passes the model only an McpException's message. This filter
// answers the two kinds of failure that are normal before the SDK sees them: a tool's own
// refusal, and an argument the SDK could not bind. Returning the error result, instead of
// throwing, is what keeps them out of the Error log; the text is the one the SDK would
// write. Any other exception passes through, so it stays an Error with a generic reply.
internal static partial class McpToolErrors
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, ct) =>
    {
        try
        {
            return await next(context, ct);
        }
        catch (Exception e) when (RefusalFor(context, e) is { } refusal)
        {
            var tool = context.Params?.Name ?? "";
            var logger = context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpToolErrors));
            // The message is not logged: it can carry what the model sent, or farm data.
            if (refusal.Argument is null)
                Refused(logger, tool);
            else
                RefusedArgument(logger, tool, refusal.Argument);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"An error occurred invoking '{tool}': {refusal.Message}" }],
            };
        }
    };

    private sealed record Refusal(string Message, string? Argument);

    // A protocol error, such as the SDK's authorization refusal, stays a JSON-RPC error. A
    // JsonException or ArgumentException is a binding failure only when an argument does
    // not fit the tool's input schema, because binding runs before the tool's body; when
    // every argument fits, the body threw it, and that is a fault.
    private static Refusal? RefusalFor(RequestContext<CallToolRequestParams> context, Exception e) => e switch
    {
        McpProtocolException => null,
        McpException => new(e.Message, null),
        JsonException or ArgumentException when context.MatchedPrimitive is McpServerTool tool =>
            BadArgument(tool.ProtocolTool.InputSchema, context.Params?.Arguments),
        _ => null,
    };

    private static Refusal? BadArgument(JsonElement schema, IDictionary<string, JsonElement>? arguments)
    {
        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray().Select(n => n.GetString()!))
                if (arguments?.ContainsKey(name) != true)
                    return new($"{name} is required.", name);

        if (arguments is null || !schema.TryGetProperty("properties", out var properties))
            return null;
        foreach (var (name, value) in arguments)
            if (properties.TryGetProperty(name, out var property) && Mismatch(name, property, value) is var (path, failed))
                return new($"{path} must be {Shape(failed)}.", path);
        return null;
    }

    // The first value that does not fit its schema: where it is, and the schema it failed.
    private static (string Path, JsonElement Schema)? Mismatch(string path, JsonElement schema, JsonElement value)
    {
        // Binding reads null as the parameter's default.
        if (value.ValueKind == JsonValueKind.Null || !schema.TryGetProperty("type", out var type))
            return null;
        if (!Types(type).Any(t => Fits(t, value)) || !FitsFormat(schema, value))
            return (path, schema);

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            return value.EnumerateArray()
                .Select((item, i) => Mismatch($"{path}[{i}]", items, item))
                .FirstOrDefault(p => p is not null);

        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
            return value.EnumerateObject()
                .Select(p => properties.TryGetProperty(p.Name, out var s) ? Mismatch($"{path}.{p.Name}", s, p.Value) : null)
                .FirstOrDefault(p => p is not null);

        return null;
    }

    private static IEnumerable<string> Types(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!) : [type.GetString()!];

    // The SDK's serializer reads integers from strings too (McpJsonUtilities.DefaultOptions).
    private static bool Fits(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out _)
            : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => true,
    };

    // The formats System.Text.Json emits for DateOnly and Guid, read as it reads them.
    private static bool FitsFormat(JsonElement schema, JsonElement value) =>
        value.ValueKind != JsonValueKind.String || !schema.TryGetProperty("format", out var format) || format.GetString() switch
        {
            "date" => DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "uuid" => value.TryGetGuid(out _),
            _ => true,
        };

    private static string Shape(JsonElement schema)
    {
        if (schema.TryGetProperty("format", out var format))
            switch (format.GetString())
            {
                case "date": return "a date, YYYY-MM-DD";
                case "uuid": return "an id";
            }

        return Types(schema.GetProperty("type")).FirstOrDefault(t => t != "null") switch
        {
            "integer" => "a whole number",
            "boolean" => "true or false",
            "string" => "text",
            "array" => "a list",
            "object" => "an object",
            _ => "a value its schema allows",
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool {Tool} refused the call.")]
    private static partial void Refused(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool {Tool} refused the call: argument {Argument} is invalid.")]
    private static partial void RefusedArgument(ILogger logger, string tool, string argument);
}
