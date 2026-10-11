using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Hosting;

// #1227 — the SDK (2.2.0, McpServerImpl) logs every exception a tool call throws at Error
// with its stack trace, and passes the model only an McpException's message. Two failures
// are normal, and this filter answers both with the error result the SDK would write, so
// neither reaches the Error log. An argument that does not bind is found before the tool
// runs, by binding each one as the SDK will. A tool's own McpException is a refusal. Any
// other exception, from the tool's construction, its body or its result, passes through
// and stays an Error with a generic reply.
internal static partial class McpToolErrors
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, ct) =>
    {
        if (context.MatchedPrimitive is McpServerTool tool && BadArgument(tool, context.Params?.Arguments) is { } bad)
            return Refuse(context, bad.Message, bad.Argument);
        try
        {
            return await next(context, ct);
        }
        catch (McpException e) when (e is not McpProtocolException)
        {
            return Refuse(context, e.Message, argument: null);
        }
    };

    private static CallToolResult Refuse(RequestContext<CallToolRequestParams> context, string message, string? argument)
    {
        var tool = context.Params?.Name ?? "";
        var logger = context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpToolErrors));
        // The message is not logged: it can carry what the model sent, or farm data.
        if (argument is null)
            Refused(logger, tool);
        else
            RefusedArgument(logger, tool, argument);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"An error occurred invoking '{tool}': {message}" }],
        };
    }

    private sealed record Refusal(string Message, string Argument);

    // AIFunctionFactory's binding (Microsoft.Extensions.AI 10.8.3): a parameter the input
    // schema lists is read from the argument of that name with the tool's serializer, and
    // a missing one takes its default or fails. The SDK puts the method first in Metadata.
    private static Refusal? BadArgument(McpServerTool tool, IDictionary<string, JsonElement>? arguments)
    {
        if (tool.Metadata.FirstOrDefault() is not MethodInfo method
            || !tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
            return null;

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.Name is not { } name || !properties.TryGetProperty(name, out var schema))
                continue;
            if (arguments?.TryGetValue(name, out var value) != true)
            {
                if (parameter.HasDefaultValue)
                    continue;
                return new($"{name} is required.", name);
            }

            try
            {
                JsonSerializer.Deserialize(value, parameter.ParameterType, McpJsonUtilities.DefaultOptions);
            }
            catch (JsonException e)
            {
                var at = Locate(new(name, parameter.ParameterType, schema, value), e.Path);
                return new($"{at.Path} must be {Shape(at)}.", at.Path);
            }
        }

        return null;
    }

    private sealed record Node(string Path, Type Type, JsonElement Schema, JsonElement? Value);

    // Follows the serializer's path to the value it refused, naming each member as the
    // schema does, whatever case the model sent. Stops at the last step it can follow.
    private static Node Locate(Node node, string? path)
    {
        var options = McpJsonUtilities.DefaultOptions;
        foreach (Match step in PathStep().Matches(path ?? ""))
        {
            var info = options.GetTypeInfo(node.Type);
            if (step.Groups["index"].Success && info.ElementType is { } element
                && node.Schema.TryGetProperty("items", out var items))
            {
                var i = int.Parse(step.Groups["index"].Value, CultureInfo.InvariantCulture);
                node = new($"{node.Path}[{i}]", element, items,
                    node.Value is { ValueKind: JsonValueKind.Array } array && i < array.GetArrayLength() ? array[i] : null);
            }
            else if (step.Groups["member"].Success
                && info.Properties.FirstOrDefault(p => string.Equals(p.Name, step.Groups["member"].Value,
                    options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) is { } member
                && node.Schema.TryGetProperty("properties", out var members)
                && members.TryGetProperty(member.Name, out var schema))
            {
                node = new($"{node.Path}.{member.Name}", member.PropertyType, schema,
                    node.Value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(step.Groups["member"].Value, out var v) ? v : null);
            }
            else
                break;
        }

        return node;
    }

    [GeneratedRegex(@"\[(?<index>\d+)\]|\.(?<member>[^.\[]+)")]
    private static partial Regex PathStep();

    private static string Shape(Node node)
    {
        if (node.Schema.TryGetProperty("format", out var format))
            switch (format.GetString())
            {
                case "date": return "a date, YYYY-MM-DD";
                case "uuid": return "an id";
            }

        var types = node.Schema.TryGetProperty("type", out var type)
            ? type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()) : [type.GetString()]
            : [];
        return types.FirstOrDefault(t => t != "null") switch
        {
            "integer" => Range(node) is { } range ? $"a whole number {range}" : "a whole number",
            "boolean" => "true or false",
            "string" => "text",
            "array" => "a list",
            "object" => "an object",
            _ => "a value its schema allows",
        };
    }

    // A whole number that still did not bind is outside its type's range.
    private static string? Range(Node node)
    {
        var text = node.Value switch
        {
            { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            _ => null,
        };
        var type = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
        return BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            && type.GetField("MinValue")?.GetValue(null) is IFormattable min
            && type.GetField("MaxValue")?.GetValue(null) is IFormattable max
                ? $"from {min.ToString(null, CultureInfo.InvariantCulture)} to {max.ToString(null, CultureInfo.InvariantCulture)}"
                : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool {Tool} refused the call.")]
    private static partial void Refused(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool {Tool} refused the call: argument {Argument} is invalid.")]
    private static partial void RefusedArgument(ILogger logger, string tool, string argument);
}
