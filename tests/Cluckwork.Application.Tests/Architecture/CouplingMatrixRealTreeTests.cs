using System.Text.RegularExpressions;
using Cluckwork.Application.Tests.TenantBypass;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class CouplingMatrixRealTreeTests
{
    [Fact]
    public void RealTree_CommittedMatrixMatchesRegeneration()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");
        var ledger = RealModuleLedger.Value;
        var edgeReport = ModuleLedgerRealTreeTests.Report.Value;
        var adapterReport = AdapterReachScanner.Scan(Path.Combine(repoRoot, "src"), ledger);
        var tableReport = ScanTables(ledger);
        var regenerated = RenderChecked(ledger, edgeReport, tableReport, adapterReport);
        var committedPath = Path.Combine(repoRoot, "tests", "Cluckwork.Application.Tests", "Architecture", "Data",
            "coupling-matrix.md");

        if (Environment.GetEnvironmentVariable("CLUCKWORK_REGENERATE_MATRIX") == "1")
        {
            File.WriteAllText(committedPath, regenerated);
        }

        AssertCommittedMatrixMatches(File.ReadAllText(committedPath), regenerated);
    }

    [Fact]
    public void RealTree_GeneratedModuleCellCensusMatchesLedgerEdges()
    {
        var ledger = RealModuleLedger.Value;
        var report = ModuleLedgerRealTreeTests.Report.Value;
        var generated = CouplingMatrix.LiveModulePairs(ledger, report).OrderBy(pair => pair.From, StringComparer.Ordinal)
            .ThenBy(pair => pair.To, StringComparer.Ordinal).ToArray();
        var declared = ledger.Edges.Select(edge => (edge.From, edge.To)).OrderBy(pair => pair.From, StringComparer.Ordinal)
            .ThenBy(pair => pair.To, StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, generated);
    }

    // docs/architecture.md groups the modules by responsibility without arrows; this keeps its node set equal to the
    // ledger's owners, so a module added, renamed or removed in the rules cannot be missed there.
    [Fact]
    public void RealTree_FeatureModuleDiagramNamesExactlyTheLedgerOwners()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");
        var document = File.ReadAllText(Path.Combine(repoRoot, "docs", "architecture.md"));
        var section = Regex.Match(document, @"^## Feature modules\r?\n.*?```mermaid\r?\n(.*?)```",
            RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(section.Success, "docs/architecture.md has no mermaid block under '## Feature modules'");
        var named = Regex.Matches(section.Groups[1].Value, @"^\s*(\w+)\[", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        var owners = RealModuleLedger.Value.Owners.Select(owner => owner.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(owners, named);
    }

    private static TableOwnerReport ScanTables(ModuleLedger ledger)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            .EnableServiceProviderCaching(false).Options;
        using var context = new AppDbContext(options, new TenantContext(), new FlockScope());
        return TableOwnerScanner.Scan(context.Model, ledger);
    }

    internal static string RenderChecked(ModuleLedger ledger, ModuleLedgerReport edges, TableOwnerReport tables,
        AdapterReachReport adapters)
    {
        var failures = ModuleLedgerScanner.Evaluate(edges).Concat(TableOwnerScanner.Evaluate(tables))
            .Concat(AdapterReachScanner.Evaluate(adapters)).ToList();
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("coupling matrix reports are invalid:\n" + string.Join("\n", failures));
        }
        return CouplingMatrix.Render(ledger, edges, tables, adapters);
    }

    internal static void AssertCommittedMatrixMatches(string committed, string regenerated)
    {
        var normalizedCommitted = NormalizeLineEndings(committed);
        var normalizedRegenerated = NormalizeLineEndings(regenerated);
        Assert.True(normalizedCommitted == normalizedRegenerated,
            "coupling matrix differs:\n" + UnifiedDiff(normalizedCommitted, normalizedRegenerated));
    }

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string UnifiedDiff(string committed, string regenerated)
    {
        var oldLines = committed.Split('\n');
        var newLines = regenerated.Split('\n');
        var builder = new System.Text.StringBuilder();
        builder.Append("--- committed coupling-matrix.md\n+++ regenerated coupling-matrix.md\n");
        var count = Math.Max(oldLines.Length, newLines.Length);
        for (var index = 0; index < count; index++)
        {
            var oldLine = index < oldLines.Length ? oldLines[index] : null;
            var newLine = index < newLines.Length ? newLines[index] : null;
            if (oldLine == newLine)
            {
                continue;
            }

            builder.Append("@@ -").Append(index + 1).Append(" +").Append(index + 1).Append(" @@\n");
            if (oldLine is not null)
            {
                builder.Append('-').Append(oldLine).Append("\n");
            }
            if (newLine is not null)
            {
                builder.Append('+').Append(newLine).Append("\n");
            }
        }
        return builder.ToString();
    }
}
