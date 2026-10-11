namespace Cluckwork.Api.Mcp;

// #807 — every read tool's reply. A silently truncated first page reads to a model as a
// complete answer, so a reply always says whether it is complete and where to continue.
// ReadAsync is the only way to build one: it asks the query for one row past the page, so
// a shorter answer proves the page is the last, and no tool chooses that row count.
public sealed record McpPage<T>
{
    public IReadOnlyList<T> Items { get; }
    public bool Complete { get; }
    public int? NextOffset { get; }

    private McpPage(IReadOnlyList<T> items, bool complete, int? nextOffset) =>
        (Items, Complete, NextOffset) = (items, complete, nextOffset);

    // fetch(take, skip, ct) returns at most take rows starting at skip.
    public static async Task<McpPage<T>> ReadAsync<TRow>(
        int limit, int offset, Func<int, int, CancellationToken, Task<IReadOnlyList<TRow>>> fetch,
        Func<TRow, T> map, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, McpPage.MaxLimit);
        // Capped so skip + take, the next offset, cannot overflow.
        var skip = Math.Clamp(offset, 0, int.MaxValue - McpPage.MaxLimit);
        var rows = await fetch(take + 1, skip, ct);
        return rows.Count > take
            ? new([.. rows.Take(take).Select(map)], complete: false, nextOffset: skip + take)
            : new([.. rows.Select(map)], complete: true, nextOffset: null);
    }
}

public static class McpPage
{
    // The REST page caps live in the endpoints, not the module contracts, so a tool sets
    // its own. Smaller than REST's: a page lands in a model's context window.
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
}
