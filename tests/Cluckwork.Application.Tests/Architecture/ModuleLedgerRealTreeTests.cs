using Cluckwork.Application.Tests.TenantBypass;

namespace Cluckwork.Application.Tests.Architecture;

// #842 — the real-tree gate; the mutation matrix reds here.


public sealed class ModuleLedgerRealTreeTests
{
    // The semantic walk binds every module-owned file, so the real-tree tests share one scan.
    internal static readonly Lazy<ModuleLedgerReport> Report = new(() => ModuleLedgerScanner.Scan(
        Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src"),
        RealModuleLedger.Value));

    [Fact]
    public void RealSourceTree_EveryCrossOwnerEdgeIsLedgered()
    {
        var report = Report.Value;
        var failures = ModuleLedgerScanner.Evaluate(report);
        Assert.True(failures.Count == 0,
            "module-ledger guard failed:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void RealSourceTree_FloorIsTheStaticMinimumNotTheScannedCount()
    {
        var report = Report.Value;

        Assert.Equal(ModuleLedgerScanner.RealTreeFileFloor, report.ExpectedFileCountFloor);
        Assert.True(report.ScannedFileCount >= ModuleLedgerScanner.RealTreeFileFloor,
            $"scanned {report.ScannedFileCount} files, floor is {ModuleLedgerScanner.RealTreeFileFloor}");
    }
}
