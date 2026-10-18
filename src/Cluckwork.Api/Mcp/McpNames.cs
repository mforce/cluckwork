using ModelContextProtocol;

namespace Cluckwork.Api.Mcp;

// #1313 — inventory items and expense categories as a model names them: text that parses as
// an id is that record, active or not, and the handler decides; a name matches an ACTIVE
// record exactly, ignoring case, as the app's pickers offer only active ones. Names are
// unique per farm, so a shared name means several farms, and it is refused with the ids.
// ponytail: matched in memory over the farm's whole list; a query when catalogs reach thousands.
internal static class McpNames
{
    public static T Resolve<T>(
        IReadOnlyList<T> all, string text, Func<T, Guid> id, Func<T, string> name, Func<T, bool> active,
        string noun, string parameter)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpException($"{parameter} is required.");
        var trimmed = text.Trim();
        if (Guid.TryParse(trimmed, out var given))
            return all.FirstOrDefault(c => id(c) == given)
                ?? throw new McpException($"No {noun} you can see has the id '{given}'.");

        var choices = all.Where(active).ToList();
        return choices.Where(c => string.Equals(name(c), trimmed, StringComparison.OrdinalIgnoreCase)).ToList() switch
        {
            [var only] => only,
            [] => throw new McpException($"No active {noun} is named '{trimmed}'. Active: {string.Join(", ", choices.Select(name))}."),
            var many => throw new McpException($"More than one active {noun} is named '{trimmed}'. Call again with "
                + $"{parameter} set to one of these ids: {string.Join(", ", many.Select(c => id(c)))}."),
        };
    }
}
