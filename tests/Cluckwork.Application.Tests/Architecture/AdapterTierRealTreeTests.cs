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
}
