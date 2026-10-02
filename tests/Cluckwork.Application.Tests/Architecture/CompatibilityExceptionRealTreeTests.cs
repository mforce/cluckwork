using Cluckwork.Application.Tests.TenantBypass;
using Xunit.Abstractions;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class CompatibilityExceptionRealTreeTests(ITestOutputHelper output)
{
    private static readonly Lazy<CompatibilityExceptionReport> Report = new(() => CompatibilityExceptionScanner.Scan(
        Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src"),
        Path.Combine(AppContext.BaseDirectory, "Architecture", "Data", "module-ledger.json")));

    [Fact]
    public void RealSourceTree_EveryCompatibilityExceptionIsRegistered()
    {
        var report = Report.Value;
        output.WriteLine(string.Join("\n", report.Reads.Select(r => $"{r.Symbol} -> {r.Reaches} [{r.Allowance}] {r.File}:{r.Line}")));
        var failures = CompatibilityExceptionScanner.Evaluate(report);
        Assert.True(failures.Count == 0, "compatibility exception guard failed:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void RealSourceTree_CompilesTheWholeSemanticProject()
    {
        var report = Report.Value;
        Assert.True(report.CompiledFileCount >= CompatibilityExceptionScanner.RealTreeFileFloor,
            $"compiled {report.CompiledFileCount} files, expected at least {CompatibilityExceptionScanner.RealTreeFileFloor}");
        Assert.Contains(report.Reads, r => r.Allowance == CompatibilityExceptionScanner.DbSetDeclaration);
        Assert.Contains(report.Reads, r => r.Allowance == CompatibilityExceptionScanner.ModulePort);
    }
}
