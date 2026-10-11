using System.Text.RegularExpressions;
using Cluckwork.Application.Tests.Documentation;

namespace Cluckwork.Application.Tests.Architecture;

// A tracked source file under src/ or web/src/ holds at most MaxLines lines; test code there (*.test.ts(x),
// *.spec.ts(x) and web/src/test/) holds at most MaxTestLines. A file already over its limit is listed with a cap: its
// size when listed plus 10% (rounded to the nearest line, halves up), at least 50 lines. A listed file over its cap
// fails, and raising a cap needs a maintainer decision. Caps are not exact counts on purpose: in the 30 days before
// #1153, 69 of 147 merged commits under src/ and web/src/ grew a file that would have been listed, so an exact count
// would be re-pinned in about half of all PRs (#632's lesson about keys that move with ordinary edits). The headroom
// absorbs ordinary edits and still stops unbounded growth. An entry whose file is gone, excluded, or back within its
// limit fails until it is deleted.
public sealed partial class FileSizeLimitTests
{
    private const int MaxLines = 500;
    private const int MaxTestLines = 1500;

    // Fails a filter that silently excludes everything, which would make the guard vacuously green.
    private const int ScannedFileFloor = 500;

    private static readonly string[] SourceRoots = ["src/", "web/src/"];
    private static readonly string[] SourceExtensions = [".cs", ".ts", ".tsx", ".css"];

    private static readonly Dictionary<string, int> Legacy = new(StringComparer.Ordinal)
    {
        ["src/Cluckwork.Api/Modules/Access/Auth/AuthEndpoints.cs"] = 711,
        ["src/Cluckwork.Api/Program.cs"] = 685,
        ["src/Cluckwork.Infrastructure/Modules/Access/Identity/IdentityProvider.cs"] = 2156,
        ["src/Cluckwork.Infrastructure/Persistence/SimulationDataSeeder.cs"] = 2785,
        ["web/src/api/client.ts"] = 1107,
        ["web/src/api/cluckwork.ts"] = 1472,
        ["web/src/components/NamedEntityPicker.tsx"] = 1531,
        ["web/src/routes/AuditPage.tsx"] = 727,
        ["web/src/routes/DailyEntryPage.tsx"] = 1365,
        ["web/src/routes/Dashboard.tsx"] = 1241,
        ["web/src/routes/ExpensesPage.tsx"] = 1082,
        ["web/src/routes/FlocksPage.tsx"] = 706,
        ["web/src/routes/HelpPage.tsx"] = 1012,
        ["web/src/routes/HistoryPage.tsx"] = 1033,
        ["web/src/routes/InventoryPage.tsx"] = 1022,
        ["web/src/routes/SettingsPage.tsx"] = 1080,
        ["web/src/routes/StockPage.tsx"] = 843,
        ["web/src/routes/UsersPage.tsx"] = 1361,
        ["web/src/routes/WaterPage.tsx"] = 751,
        ["web/src/styles.css"] = 3581,
        ["web/src/theme/FarmThemeProvider.tsx"] = 793,
    };

    private static readonly Dictionary<string, int> LegacyTests = new(StringComparer.Ordinal)
    {
        ["web/src/api/client.test.ts"] = 2636,
        ["web/src/components/NamedEntityPicker.test.tsx"] = 1773,
        ["web/src/routes/AuditPage.test.tsx"] = 2105,
        ["web/src/routes/DailyEntryPage.test.tsx"] = 2105,
        ["web/src/routes/Dashboard.test.tsx"] = 2687,
        ["web/src/routes/ExpensesPage.test.tsx"] = 1978,
        ["web/src/routes/HistoryPage.test.tsx"] = 1751,
        ["web/src/routes/InventoryPage.test.tsx"] = 1916,
        ["web/src/routes/SettingsPage.test.tsx"] = 2044,
        ["web/src/routes/StockPage.test.tsx"] = 1929,
        ["web/src/routes/UsersPage.test.tsx"] = 4418,
        ["web/src/routes/sales/SalesPage.test.tsx"] = 5353,
    };

    [GeneratedRegex(@"^web/src/i18n/[a-z]{2}\.ts$")]
    private static partial Regex LocaleCatalog();

    [GeneratedRegex(@"(^|/)tests?/|\.(test|spec)\.tsx?$")]
    private static partial Regex TestFile();

    // Generated or data-shaped files that are long by nature: EF migrations with their Designer files and the model
    // snapshot, all under Migrations/, and the per-locale i18n catalogs.
    private static bool IsExcluded(string path) =>
        path.Contains("/Migrations/", StringComparison.Ordinal) || LocaleCatalog().IsMatch(path);

    [Fact]
    public void EveryTrackedSourceFile_StaysWithinTheLineLimit()
    {
        var root = TenancyDocsFreshnessTests.RepoRoot();
        var counts = TenancyDocsFreshnessTests.TrackedFiles(root)
            .Where(p => SourceRoots.Any(r => p.StartsWith(r, StringComparison.Ordinal)))
            .Where(p => SourceExtensions.Any(e => p.EndsWith(e, StringComparison.Ordinal)))
            .Where(p => !IsExcluded(p))
            .ToDictionary(p => p, p => File.ReadAllLines(Path.Combine(root, p)).Length, StringComparer.Ordinal);

        Assert.True(counts.Count >= ScannedFileFloor,
            $"Scanned {counts.Count} source files, below the floor of {ScannedFileFloor}; a filter is excluding too much.");

        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, lines) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var (max, legacy) = TestFile().IsMatch(path) ? (MaxTestLines, LegacyTests) : (MaxLines, Legacy);
            if (!legacy.TryGetValue(path, out var cap))
            {
                if (lines > max)
                    failures.Add($"{path}: {lines} lines, over the {max}-line limit. Split it.");
                continue;
            }
            seen.Add(path);
            if (lines <= max)
                failures.Add($"{path}: now {lines} lines, within the {max}-line limit. Delete its legacy entry.");
            else if (lines > cap)
                failures.Add($"{path}: {lines} lines, over its legacy cap of {cap}. Split it; raising a cap needs a maintainer decision.");
        }
        foreach (var path in Legacy.Keys.Concat(LegacyTests.Keys).Where(p => !seen.Contains(p)).Order(StringComparer.Ordinal))
            failures.Add($"{path}: listed as legacy but not a scanned file of its kind. Delete its entry.");

        Assert.True(failures.Count == 0, "File-size limit violations:\n  " + string.Join("\n  ", failures));
    }
}
