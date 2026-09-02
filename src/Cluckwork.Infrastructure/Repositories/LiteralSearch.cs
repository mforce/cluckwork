namespace Cluckwork.Infrastructure.Repositories;

using Microsoft.EntityFrameworkCore;

// #512 — literal name search, shared by the flock and customer discovery
// queries because the two routes must not drift on the one thing the picker
// contract promises: what the user typed is data, never pattern syntax.
//
// The helper is deliberately one-sided: callers pass the RAW query and get back
// a finished pattern, so a repository cannot escape before this runs (which
// would double-escape) or after it wraps the wildcards (which would leave the
// user's `%` and `_` live — the defect this exists to prevent).
//
// The three-argument EF.Functions.ILike is what makes the contract executable
// in SQL: Npgsql translates it to `ILIKE ... ESCAPE '\'`, so the case folding,
// the substring match and the escape all happen server-side and before the
// window is cut. `ToLower().Contains(...)` would move the case decision into a
// collation and hide the wildcard handling from the query entirely.
//
// Order matters: the escape character itself is escaped FIRST, so the
// backslashes that step inserts are not re-escaped by the ones after it.
internal static class LiteralSearch
{
    // Npgsql's EF.Functions.ILike(match, pattern, escape) takes the escape as a
    // STRING, matching SQL's `ESCAPE ''` clause; one backslash, one character.
    public const string EscapeChar = "\\";

    // Blank (missing, empty, or whitespace-only) is an unfiltered search — the
    // caller skips the predicate rather than matching against an empty string,
    // which a `LIKE '%%'` would answer with "everything, archived included".
    public static string? Normalize(string? search) =>
        string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    // '%'+escaped+'%': the wildcards are appended AFTER escaping, so they are
    // the only unescaped metacharacters in the pattern.
    public static string ContainsPattern(string trimmed) =>
        "%" + trimmed
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_") + "%";
}
