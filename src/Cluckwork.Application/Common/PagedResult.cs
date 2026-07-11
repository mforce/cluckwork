namespace Cluckwork.Application.Common;

// Cursor-based pagination — never offset for large tables (tech spec §3.2).
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);

public sealed record CursorPage(string? Cursor = null, int Limit = 50);
