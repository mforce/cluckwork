namespace Cluckwork.Api.Mcp;

// #807 — every read tool's reply. A silently truncated first page reads to a model as a
// complete answer, so a reply always says whether it is complete and where to continue.
public sealed record McpPage<T>(IReadOnlyList<T> Items, bool Complete, int? NextOffset);

public static class McpPage
{
    // The REST page caps live in the endpoints, not the module contracts, so a tool sets
    // its own. Smaller than REST's: a page lands in a model's context window.
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public static (int Take, int Skip) Window(int limit, int offset) =>
        (Math.Clamp(limit, 1, MaxLimit), Math.Max(offset, 0));

    // fetched holds up to take + 1 rows; the extra row only proves there is more.
    public static McpPage<TOut> From<TIn, TOut>(IReadOnlyList<TIn> fetched, int take, int skip, Func<TIn, TOut> map) =>
        fetched.Count > take
            ? new([.. fetched.Take(take).Select(map)], Complete: false, NextOffset: skip + take)
            : new([.. fetched.Select(map)], Complete: true, NextOffset: null);
}
