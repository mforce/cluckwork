using System.Reflection;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;

namespace Cluckwork.Application.Tests.Architecture;

// #849: every type a ledger owner lists as its contract carries no persistence
// type and no domain entity or aggregate, however deeply nested.
public sealed class ModuleContractRealAssemblyTests
{
    private static readonly Assembly[] Assemblies = [typeof(IUnitOfWork).Assembly, typeof(Result).Assembly];

    [Fact]
    public void RealLedger_EveryContractTypeIsFreeOfPersistenceAndAggregates()
    {
        var names = RealModuleLedger.Value.Owners.SelectMany(o => o.Contract).ToList();
        var types = names.Select(name => Assemblies.Select(a => a.GetType(name)).OfType<Type>().SingleOrDefault()).ToList();
        Assert.True(types.All(t => t is not null),
            "contract types not found in Application or Domain: " +
            string.Join(", ", names.Where((_, i) => types[i] is null)));

        // Infrastructure implements Application's ports, so a Result subclass it returns reaches callers as a Result.
        var report = SeamSurfaceScanner.ScanContracts(
            types!, minimumInterfaceFloor: 1, knownAssemblies: [.. Assemblies, typeof(AppDbContext).Assembly]);
        var failures = SeamSurfaceScanner.Evaluate(report);
        Assert.True(failures.Count == 0, "module contract guard failed:\n  " + string.Join("\n  ", failures));
        Assert.Contains("Cluckwork.Application.Modules.Finance.Contracts.IFinanceModule", report.InspectedInterfaces);
    }
}
