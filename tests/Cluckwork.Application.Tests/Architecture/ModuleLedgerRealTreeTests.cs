namespace Cluckwork.Application.Tests.Architecture;

// #842 — the real-tree gate; the mutation matrix reds here.

using Cluckwork.Application.Tests.TenantBypass;

public sealed class ModuleLedgerRealTreeTests
{
    private static string SrcRoot() =>
        Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src");

    private static string LedgerPath() =>
        Path.Combine(AppContext.BaseDirectory, "Architecture", "Data", "module-ledger.json");

    [Fact]
    public void RealSourceTree_EveryCrossOwnerEdgeIsLedgered()
    {
        var report = ModuleLedgerScanner.Scan(SrcRoot(), LedgerPath());
        var failures = ModuleLedgerScanner.Evaluate(report);
        Assert.True(failures.Count == 0,
            "module-ledger guard failed:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void RealSourceTree_FloorIsTheStaticMinimumNotTheScannedCount()
    {
        var report = ModuleLedgerScanner.Scan(SrcRoot(), LedgerPath());

        Assert.Equal(ModuleLedgerScanner.RealTreeFileFloor, report.ExpectedFileCountFloor);
        Assert.True(report.ScannedFileCount >= ModuleLedgerScanner.RealTreeFileFloor,
            $"scanned {report.ScannedFileCount} files, floor is {ModuleLedgerScanner.RealTreeFileFloor}");
    }
}
