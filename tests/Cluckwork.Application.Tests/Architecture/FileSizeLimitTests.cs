using System.Text.RegularExpressions;
using Cluckwork.Application.Tests.Documentation;

namespace Cluckwork.Application.Tests.Architecture;

// A tracked non-test source file under src/ or web/src/ holds at most MaxLines lines. Files already over the limit
// are listed by name and may change freely; the list only shrinks. An entry whose file is gone, excluded, or back
// within the limit fails until it is deleted. Line counts are deliberately not pinned: in the 30 days before this
// guard, 69 of 147 merged commits under src/ and web/src/ grew a file that would have been listed, so a per-file
// count would be raised in the same PR about half the time (#632's lesson about keys that move with ordinary edits).
public sealed partial class FileSizeLimitTests
{
    private const int MaxLines = 500;

    // Fails a filter that silently excludes everything, which would make the guard vacuously green.
    private const int ScannedFileFloor = 500;

    private static readonly string[] SourceRoots = ["src/", "web/src/"];
    private static readonly string[] SourceExtensions = [".cs", ".ts", ".tsx", ".css"];

    private static readonly HashSet<string> Legacy = new(StringComparer.Ordinal)
    {
        "src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs",
        "src/Cluckwork.Api/Modules/Access/Auth/AuthEndpoints.cs",
        "src/Cluckwork.Api/Program.cs",
        "src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrder.cs",
        "src/Cluckwork.Domain/Modules/Farm/Media/ImageSanitizer.cs",
        "src/Cluckwork.Infrastructure/Modules/Access/Identity/IdentityProvider.cs",
        "src/Cluckwork.Infrastructure/Persistence/SimulationDataSeeder.cs",
        "web/src/api/client.ts",
        "web/src/api/cluckwork.ts",
        "web/src/components/NamedEntityPicker.tsx",
        "web/src/i18n/enums.ts",
        "web/src/routes/AuditPage.tsx",
        "web/src/routes/DailyEntryPage.tsx",
        "web/src/routes/Dashboard.tsx",
        "web/src/routes/ExpensesPage.tsx",
        "web/src/routes/FeedPage.tsx",
        "web/src/routes/FlocksPage.tsx",
        "web/src/routes/HelpPage.tsx",
        "web/src/routes/HistoryPage.tsx",
        "web/src/routes/InventoryPage.tsx",
        "web/src/routes/ProductsPage.tsx",
        "web/src/routes/SalesPage.tsx",
        "web/src/routes/SettingsPage.tsx",
        "web/src/routes/StockPage.tsx",
        "web/src/routes/UsersPage.tsx",
        "web/src/routes/WaterPage.tsx",
        "web/src/styles.css",
        "web/src/theme/FarmThemeProvider.tsx",
    };

    [GeneratedRegex(@"^web/src/i18n/[a-z]{2}\.ts$")]
    private static partial Regex LocaleCatalog();

    [GeneratedRegex(@"(^|/)tests?/|\.(test|spec)\.tsx?$")]
    private static partial Regex TestFile();

    // Generated or data-shaped files that are long by nature (EF migrations with their Designer files and the model
    // snapshot, all under Migrations/, and the per-locale i18n catalogs), and test code, which the cap does not cover.
    private static bool IsExcluded(string path) =>
        path.Contains("/Migrations/", StringComparison.Ordinal) || LocaleCatalog().IsMatch(path) || TestFile().IsMatch(path);

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
        foreach (var (path, lines) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var listed = Legacy.Contains(path);
            if (!listed && lines > MaxLines)
                failures.Add($"{path}: {lines} lines, over the {MaxLines}-line limit. Split it.");
            else if (listed && lines <= MaxLines)
                failures.Add($"{path}: now {lines} lines, within the limit. Delete its legacy entry.");
        }
        foreach (var path in Legacy.Where(p => !counts.ContainsKey(p)).Order(StringComparer.Ordinal))
            failures.Add($"{path}: listed as legacy but not a scanned source file. Delete its entry.");

        Assert.True(failures.Count == 0, "File-size limit violations:\n  " + string.Join("\n  ", failures));
    }
}
