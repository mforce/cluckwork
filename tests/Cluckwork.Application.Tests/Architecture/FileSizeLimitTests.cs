using System.Text.RegularExpressions;
using Cluckwork.Application.Tests.Documentation;

namespace Cluckwork.Application.Tests.Architecture;

// A tracked source file under src/ or web/src/ holds at most MaxLines lines. Files already over the limit are
// grandfathered at their current count, and that count only goes down: growing fails, shrinking without lowering the
// entry fails, and an entry at or under the limit must be deleted.
public sealed partial class FileSizeLimitTests
{
    private const int MaxLines = 500;

    // Fails a filter that silently excludes everything, which would make the guard vacuously green.
    private const int ScannedFileFloor = 700;

    private static readonly string[] SourceRoots = ["src/", "web/src/"];
    private static readonly string[] SourceExtensions = [".cs", ".ts", ".tsx", ".css"];

    private static readonly Dictionary<string, int> AllowList = new(StringComparer.Ordinal)
    {
        ["src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs"] = 606,
        ["src/Cluckwork.Api/Modules/Access/Auth/AuthEndpoints.cs"] = 646,
        ["src/Cluckwork.Api/Program.cs"] = 621,
        ["src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrder.cs"] = 539,
        ["src/Cluckwork.Domain/Modules/Farm/Media/ImageSanitizer.cs"] = 675,
        ["src/Cluckwork.Infrastructure/Modules/Access/Identity/IdentityProvider.cs"] = 1960,
        ["src/Cluckwork.Infrastructure/Persistence/SimulationDataSeeder.cs"] = 2532,
        ["web/src/api/client.test.ts"] = 2390,
        ["web/src/api/client.ts"] = 1006,
        ["web/src/api/cluckwork.ts"] = 1338,
        ["web/src/auth/AuthContext.lifecycle.test.tsx"] = 523,
        ["web/src/components/Dialog.test.tsx"] = 689,
        ["web/src/components/NamedEntityPicker.test.tsx"] = 1612,
        ["web/src/components/NamedEntityPicker.tsx"] = 1392,
        ["web/src/components/NumberField.test.tsx"] = 517,
        ["web/src/components/useConfirm.test.tsx"] = 520,
        ["web/src/components/usePagedList.test.tsx"] = 985,
        ["web/src/i18n/enums.ts"] = 582,
        ["web/src/routes/AuditPage.test.tsx"] = 1914,
        ["web/src/routes/AuditPage.tsx"] = 661,
        ["web/src/routes/CustomersPage.test.tsx"] = 1194,
        ["web/src/routes/DailyEntryPage.test.tsx"] = 1882,
        ["web/src/routes/DailyEntryPage.tsx"] = 1237,
        ["web/src/routes/Dashboard.test.tsx"] = 2431,
        ["web/src/routes/Dashboard.tsx"] = 1128,
        ["web/src/routes/ExpensesPage.test.tsx"] = 1798,
        ["web/src/routes/ExpensesPage.tsx"] = 984,
        ["web/src/routes/FeedPage.test.tsx"] = 526,
        ["web/src/routes/FeedPage.tsx"] = 522,
        ["web/src/routes/FlocksPage.test.tsx"] = 1273,
        ["web/src/routes/FlocksPage.tsx"] = 642,
        ["web/src/routes/GradesPage.test.tsx"] = 737,
        ["web/src/routes/HelpPage.test.tsx"] = 1292,
        ["web/src/routes/HelpPage.tsx"] = 903,
        ["web/src/routes/HistoryPage.test.tsx"] = 1582,
        ["web/src/routes/HistoryPage.tsx"] = 939,
        ["web/src/routes/InventoryPage.test.tsx"] = 1738,
        ["web/src/routes/InventoryPage.tsx"] = 929,
        ["web/src/routes/Login.test.tsx"] = 830,
        ["web/src/routes/ProductsPage.test.tsx"] = 959,
        ["web/src/routes/ProductsPage.tsx"] = 636,
        ["web/src/routes/ReportsPage.test.tsx"] = 570,
        ["web/src/routes/SettingsPage.test.tsx"] = 1858,
        ["web/src/routes/SettingsPage.tsx"] = 982,
        ["web/src/routes/StockPage.test.tsx"] = 1754,
        ["web/src/routes/StockPage.tsx"] = 766,
        ["web/src/routes/UsersPage.test.tsx"] = 4016,
        ["web/src/routes/UsersPage.tsx"] = 1237,
        ["web/src/routes/WaterPage.test.tsx"] = 847,
        ["web/src/routes/WaterPage.tsx"] = 683,
        ["web/src/routes/sales/SalesPage.test.tsx"] = 4706,
        ["web/src/styles.css"] = 3229,
        ["web/src/theme/farmTheme.policy.test.ts"] = 533,
        ["web/src/theme/FarmThemeProvider.tsx"] = 725,
    };

    [GeneratedRegex(@"^web/src/i18n/[a-z]{2}\.ts$")]
    private static partial Regex LocaleCatalog();

    // Generated or data-shaped files that are long by nature: EF migrations with their Designer files and the model
    // snapshot, all under Migrations/, and the per-locale i18n catalogs.
    private static bool IsExcluded(string path) =>
        path.Contains("/Migrations/", StringComparison.Ordinal) || LocaleCatalog().IsMatch(path);

    [Fact]
    public void EveryTrackedSourceFile_StaysWithinItsLineLimit()
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
            if (!AllowList.TryGetValue(path, out var allowed))
            {
                if (lines > MaxLines)
                    failures.Add($"{path}: {lines} lines, over the {MaxLines}-line limit. Split it.");
            }
            else if (lines > allowed)
                failures.Add($"{path}: grew to {lines} lines, above its allow-list entry of {allowed}. Shrink it.");
            else if (lines <= MaxLines)
                failures.Add($"{path}: now {lines} lines, within the limit. Delete its allow-list entry.");
            else if (lines < allowed)
                failures.Add($"{path}: shrank to {lines} lines. Lower its allow-list entry from {allowed} to {lines}.");
        }
        foreach (var path in AllowList.Keys.Where(p => !counts.ContainsKey(p)).Order(StringComparer.Ordinal))
            failures.Add($"{path}: allow-listed but not a scanned source file. Delete its entry.");

        Assert.True(failures.Count == 0, "File-size limit violations:\n  " + string.Join("\n  ", failures));
    }
}
