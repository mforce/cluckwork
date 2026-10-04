using Cluckwork.Application.Tests.TenantBypass;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class AdapterTierRealTreeTests
{
    private static AdapterTierReport Scan() => AdapterTierScanner.Scan(
        Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src"),
        RealModuleLedger.Value);

    [Fact]
    public void RealSourceTree_HasNoViolations()
    {
        var report = Scan();
        Assert.True(AdapterTierScanner.Evaluate(report).Count == 0,
            "adapter tier guard failed:\n" + string.Join("\n", AdapterTierScanner.Evaluate(report)));
    }

    [Fact]
    public void McpTierRow_IsDeclared()
    {
        Assert.Contains(RealModuleLedger.Value.AdapterTiers, t => t.Namespace == "Cluckwork.Api.Mcp" && t.Surface == "MapMcp");
    }

    // /mcp is not mapped yet (#806 has not landed). This pins today's green: the
    // row is committed but its namespace holds no tool type and MapMcp is not
    // invoked. It goes stale the day #806 maps /mcp under this namespace — that
    // author drops this assertion, not works around it.
    [Fact]
    public void McpTierRow_IsDormantToday()
    {
        var report = Scan();
        Assert.Contains(report.Dormant, t => t.Namespace == "Cluckwork.Api.Mcp");
    }
}
