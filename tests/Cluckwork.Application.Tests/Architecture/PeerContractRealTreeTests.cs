using Cluckwork.Application.Tests.TenantBypass;
using Xunit.Abstractions;

namespace Cluckwork.Application.Tests.Architecture;

// #1023: every module reaches a contracted peer only through the peer's contract or seam. The adapter walk
// runs with each module's own namespaces as roots, so it reads the same parameter types and service
// resolutions it reads for endpoints, and nothing in method bodies.
public sealed class PeerContractRealTreeTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryModule_ReachesAContractedPeerOnlyThroughItsContractOrSeam()
    {
        var report = AdapterReachScanner.ScanPeers(
            Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException("repo root not found"), "src"),
            RealModuleLedger.Value);
        output.WriteLine($"Walked {report.WalkedAdapterCount} module members.");

        Assert.Equal(800, report.ExpectedAdapterCountFloor);
        Assert.Contains(report.LiveReach, r =>
            r.Symbol == "Cluckwork.Application.Features.Sales.ConfirmSale.ConfirmSaleHandler.ctor"
            && r.Type == "Cluckwork.Application.Features.EggLots.IEggStock");
        Assert.Contains(report.LiveReach, r =>
            r.Symbol == "Cluckwork.Application.Features.Expenses.CreateExpense.CreateExpenseHandler.ctor"
            && r.Type == "Cluckwork.Application.Features.Accounts.IAccountRepository");
        var failures = AdapterReachScanner.Evaluate(report);
        Assert.True(failures.Count == 0, "peer contract guard failed:\n" + string.Join("\n", failures));
    }
}
