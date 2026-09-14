namespace Cluckwork.Application.Tests.Architecture;

using Cluckwork.Application.Tests.TenantBypass;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class CouplingMatrixRealTreeTests
{
    [Fact]
    public void RealTree_CommittedMatrixMatchesRegeneration()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");
        var ledgerPath = Path.Combine(AppContext.BaseDirectory, "Architecture", "Data", "module-ledger.json");
        var ledger = ModuleLedger.Load(ledgerPath);
        var edgeReport = ModuleLedgerScanner.Scan(Path.Combine(repoRoot, "src"), ledgerPath);
        var adapterReport = AdapterReachScanner.Scan(Path.Combine(repoRoot, "src"), ledgerPath);
        var tableReport = ScanTables(ledger);
        var regenerated = CouplingMatrix.Render(ledger, edgeReport, tableReport, adapterReport);
        var committedPath = Path.Combine(repoRoot, "tests", "Cluckwork.Application.Tests", "Architecture", "Data",
            "coupling-matrix.md");

        if (Environment.GetEnvironmentVariable("CLUCKWORK_REGENERATE_MATRIX") == "1")
        {
            File.WriteAllText(committedPath, regenerated);
        }

        var committed = File.ReadAllText(committedPath);
        Assert.True(committed == regenerated, "coupling matrix differs:\n" + UnifiedDiff(committed, regenerated));
    }

    [Fact]
    public void RealTree_GeneratedModuleCellCensusMatchesLedgerEdges()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");
        var ledgerPath = Path.Combine(AppContext.BaseDirectory, "Architecture", "Data", "module-ledger.json");
        var ledger = ModuleLedger.Load(ledgerPath);
        var report = ModuleLedgerScanner.Scan(Path.Combine(repoRoot, "src"), ledgerPath);
        var generated = CouplingMatrix.LiveModulePairs(ledger, report).OrderBy(pair => pair.From, StringComparer.Ordinal)
            .ThenBy(pair => pair.To, StringComparer.Ordinal).ToArray();
        var declared = ledger.Edges.Select(edge => (edge.From, edge.To)).OrderBy(pair => pair.From, StringComparer.Ordinal)
            .ThenBy(pair => pair.To, StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, generated);
    }

    private static TableOwnerReport ScanTables(ModuleLedger ledger)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            .EnableServiceProviderCaching(false).Options;
        using var context = new AppDbContext(options, new TenantContext(), new FlockScope());
        return TableOwnerScanner.Scan(context.Model, ledger);
    }

    private static string UnifiedDiff(string committed, string regenerated)
    {
        var oldLines = committed.Split('\n');
        var newLines = regenerated.Split('\n');
        var builder = new System.Text.StringBuilder("--- committed coupling-matrix.md\n+++ regenerated coupling-matrix.md\n");
        var count = Math.Max(oldLines.Length, newLines.Length);
        for (var index = 0; index < count; index++)
        {
            var oldLine = index < oldLines.Length ? oldLines[index] : null;
            var newLine = index < newLines.Length ? newLines[index] : null;
            if (oldLine == newLine)
            {
                continue;
            }

            builder.Append("@@ -").Append(index + 1).Append(" +").Append(index + 1).AppendLine(" @@");
            if (oldLine is not null)
            {
                builder.Append('-').AppendLine(oldLine);
            }
            if (newLine is not null)
            {
                builder.Append('+').AppendLine(newLine);
            }
        }
        return builder.ToString();
    }
}
