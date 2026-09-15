namespace Cluckwork.Application.Tests.Architecture;

using Cluckwork.Application.Tests.TenantBypass;
using Xunit.Abstractions;

public sealed class AdapterReachRealTreeTests(ITestOutputHelper output)
{
    private static AdapterReachReport Scan() => AdapterReachScanner.Scan(
        Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src"),
        Path.Combine(AppContext.BaseDirectory, "Architecture", "Data", "module-ledger.json"));

    [Fact]
    public void RealSourceTree_EveryAdapterReachIsDeclared()
    {
        var report = Scan();
        output.WriteLine($"Walked {report.WalkedAdapterCount} adapters; {report.LiveReach.Select(r => r.Symbol).Distinct().Count()} non-empty adapter rows.");
        output.WriteLine($"Top-level Program adapters: {report.TopLevelProgramAdapterCount}.");
        output.WriteLine("Loosenable:\n" + DescribeLoosenable(report));
        var failures = AdapterReachScanner.Evaluate(report);
        Assert.True(failures.Count == 0, "adapter reach guard failed:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void RealSourceTree_WalksAtLeastFortyAdapters()
    {
        var report = Scan();
        Assert.Equal(40, report.ExpectedAdapterCountFloor);
        Assert.True(report.WalkedAdapterCount >= 40, $"walked {report.WalkedAdapterCount} adapters, expected at least 40");
    }

    internal static void AssertNoLoosenable(AdapterReachReport report) =>
        Assert.True(report.Loosenable.Count == 0, "Loosenable:\n" + DescribeLoosenable(report));

    private static string DescribeLoosenable(AdapterReachReport report) =>
        string.Join("\n", report.Loosenable.Select(a => $"{a.Symbol} -> {string.Join(", ", a.Reaches)}"));
}
