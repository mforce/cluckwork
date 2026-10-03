using System.Text.Json.Nodes;
using Cluckwork.Application.Tests.TenantBypass;

namespace Cluckwork.Application.Tests.Architecture;

// #854, the first peer check #1023 asks for: Commerce reaches Egg Operations
// only through owners.EggOperations.contract. Commerce's own namespaces are
// walked as adapter roots, so the adapter scanner reads the same parameter
// types and service resolutions it reads for endpoints. Only the Egg Operations
// reach is checked: Commerce calls Farm's IAccountRepository and Access's
// assignment repository on purpose (#851, #857).
public sealed class PeerContractRealTreeTests
{
    [Fact]
    public void Commerce_ReachesEggOperationsOnlyThroughItsContract()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");
        var ledger = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Architecture", "Data", "module-ledger.json")))!;
        ledger["adapterRoots"]!["namespaces"] = ledger["owners"]!["Commerce"]!["namespaces"]!.DeepClone();
        ledger["adapterRoots"]!["types"] = new JsonArray();
        ledger["adapterRoots"]!["topLevelPrograms"] = new JsonArray();
        var ledgerPath = Path.Combine(Path.GetTempPath(), $"commerce-peer-ledger-{Guid.NewGuid():N}.json");
        File.WriteAllText(ledgerPath, ledger.ToJsonString());
        try
        {
            var report = AdapterReachScanner.Scan(Path.Combine(repoRoot, "src"), ledgerPath);

            Assert.Contains(report.LiveReach, r =>
                r.Symbol == "Cluckwork.Application.Features.Sales.ConfirmSale.ConfirmSaleHandler.ctor"
                && r.Type == "Cluckwork.Application.Features.EggLots.IEggStock");
            var bypasses = report.ContractBypasses.Where(r => r.Owner == "EggOperations")
                .Select(r => $"{r.Symbol} through {r.Type} at {r.File}:{r.Line}").ToList();
            Assert.True(bypasses.Count == 0,
                "Commerce reaches Egg Operations outside its contract:\n" + string.Join("\n", bypasses));
        }
        finally
        {
            File.Delete(ledgerPath);
        }
    }
}
