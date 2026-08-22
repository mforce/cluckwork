namespace Cluckwork.Application.Tests.TenantBypass;

using System.Text.Json;
using System.Text.Json.Serialization;

// #536 Part 1 — the allow-list. One committed, reviewable line per excused
// bypass (design M5/M7): the exemption is a SEPARATE artifact from the code it
// excuses, so a bypass and its exemption are never the same keystroke.
//
// Rules:
//  * Entries name the enclosing method in symbol display form
//    (Namespace.Type.Method(paramTypes)); a call inside a local function keys
//    as ContainingMethod.Local(localFunctionName) and is NOT covered by the
//    parent's entry.
//  * An entry matching zero sites is STALE and fails the build (a deleted
//    bypass must not leave a live exemption).
//  * Justification is mandatory and non-empty — an unexplained exemption is
//    the thing the guard exists to prevent.
public sealed class AllowListEntry
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("justification")]
    public required string Justification { get; init; }
}

public static class AllowList
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<AllowListEntry> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<AllowListEntry>>(json, Options) ?? [];
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol)
                && !string.IsNullOrWhiteSpace(e.File)
                && !string.IsNullOrWhiteSpace(e.Justification))
            .ToList();
    }
}
