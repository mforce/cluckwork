namespace Cluckwork.Application.Tests.Architecture;

// #1023: the peer rule on a temp tree, one named assertion per allowance and registry error.
public sealed class PeerContractTests : IDisposable
{
    private const string FarmSeam = "Cluckwork.Temp.Farm.IAccountRepository";
    private const string FlockContract = "Cluckwork.Temp.Flocks.IFlockLookup";

    private readonly string _tempRoot = Directory.CreateTempSubdirectory("peer-contract-").FullName;

    public PeerContractTests()
    {
        WriteSource("Farm.cs", """
            namespace Cluckwork.Temp.Farm;
            public interface IFarmModule { }
            public interface IAccountRepository { }
            """);
        WriteSource("Flocks.cs", """
            namespace Cluckwork.Temp.Flocks;
            public interface IFlockLookup { }
            public interface IFlockRepository { }
            """);
        WriteSource("Finance.cs", """
            namespace Cluckwork.Temp.Finance;
            public interface IExpenseRepository { }
            """);
        WriteSource("Access.cs", """
            namespace Cluckwork.Temp.Access;
            public interface IAccessModule { }
            """);
        WriteSource("Common.cs", """
            namespace Cluckwork.Temp.Common;
            public interface IClock { }
            """);
    }

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private void WriteSource(string relativePath, string content)
    {
        var full = Path.Combine(_tempRoot, "src", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static IReadOnlyList<string> OneOrNone(string entry) => entry == "" ? [] : [entry];

    private static ModuleLedger Ledger(string farmSeam, string flockContract) =>
        ModuleLedger.Validate(new ModuleLedger(
            [
                new("Hub", "platform", ["Cluckwork.Temp", "Cluckwork.Temp.Flocks.Shared"], []),
                new("Farm", "module", ["Cluckwork.Temp.Farm"], [])
                {
                    Contract = ["Cluckwork.Temp.Farm.IFarmModule"], Seam = OneOrNone(farmSeam),
                },
                new("FlockManagement", "module", ["Cluckwork.Temp.Flocks"], []) { Contract = OneOrNone(flockContract) },
                new("Finance", "module", ["Cluckwork.Temp.Finance"], []),
                new("Access", "module", ["Cluckwork.Temp.Access"], []) { Contract = ["Cluckwork.Temp.Access.IAccessModule"] },
            ],
            [], [])
        {
            AdapterRoots = new(["Cluckwork.Temp.Endpoints"], [])
            {
                PersistenceForbiddenNamespaces = ["Cluckwork.Temp.Endpoints"],
            },
        });

    private IReadOnlyList<string> Peers(
        string farmSeam = FarmSeam, string flockContract = FlockContract) =>
        AdapterReachScanner.Evaluate(AdapterReachScanner.ScanPeers(
            Path.Combine(_tempRoot, "src"), Ledger(farmSeam, flockContract)));

    [Fact]
    public void PeerNonContractType_IsABypassNamingMemberTypeAndLocation()
    {
        WriteSource("Expense.cs", """
            namespace Cluckwork.Temp.Finance;
            public class ExpenseHandler(Cluckwork.Temp.Flocks.IFlockLookup lookup, Cluckwork.Temp.Flocks.IFlockRepository flocks) { }
            """);

        var failure = Assert.Single(Peers());
        Assert.Contains("contract bypass Cluckwork.Temp.Finance.ExpenseHandler.ctor -> FlockManagement " +
            "through Cluckwork.Temp.Flocks.IFlockRepository at src/Expense.cs:2", failure);
    }

    [Fact]
    public void SeamType_IsGreenForPeersOnlyWhileTheSeamListsIt()
    {
        WriteSource("Expense.cs", """
            namespace Cluckwork.Temp.Finance;
            public class ExpenseHandler { public void Handle(Cluckwork.Temp.Farm.IAccountRepository accounts) { } }
            """);

        Assert.Empty(Peers());
        Assert.Contains("-> Farm through Cluckwork.Temp.Farm.IAccountRepository", Assert.Single(Peers(farmSeam: "")));
    }

    [Fact]
    public void OwnModuleUncontractedTargetsAndPlatformUnderAModuleRoot_AreNotChecked()
    {
        WriteSource("Mixed.cs", """
            namespace Cluckwork.Temp.Flocks
            {
                public class FlockHandler(IFlockRepository flocks, Cluckwork.Temp.Finance.IExpenseRepository expenses,
                    Cluckwork.Temp.Common.IClock clock) { }
            }
            namespace Cluckwork.Temp.Flocks.Shared
            {
                public class Helper(Cluckwork.Temp.Flocks.IFlockRepository flocks) { }
            }
            """);

        Assert.Empty(Peers());
    }

    [Fact]
    public void Seam_DoesNotApplyToAdapters()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run(Cluckwork.Temp.Farm.IAccountRepository accounts) { } }
            """);

        var report = AdapterReachScanner.Scan(Path.Combine(_tempRoot, "src"), Ledger(FarmSeam, FlockContract));
        Assert.Equal("Cluckwork.Temp.Farm.IAccountRepository", Assert.Single(report.ContractBypasses).Type);
    }

    [Theory]
    [InlineData("Cluckwork.Temp.Farm.IGone", "owner 'Farm' seam type 'Cluckwork.Temp.Farm.IGone' is not declared under src/")]
    [InlineData("Cluckwork.Temp.Flocks.IFlockRepository", "owner 'Farm' seam type 'Cluckwork.Temp.Flocks.IFlockRepository' is not in a namespace 'Farm' owns")]
    public void BadSeamType_IsARegistryError(string entry, string expected) =>
        Assert.Contains(expected, Assert.Single(Peers(farmSeam: entry)));

    [Theory]
    [InlineData("contract", "Farm.cs", "Cluckwork.Temp.Farm", "IFarmModule")]
    [InlineData("seam", "Farm.cs", "Cluckwork.Temp.Farm", "IAccountRepository")]
    public void GenericHomonymOfAnEntry_IsARegistryError(string list, string file, string ns, string type)
    {
        WriteSource("Homonym" + file, $"namespace {ns}; public interface {type}<T> {{ }}");

        Assert.Contains($"owner 'Farm' {list} type '{ns}.{type}' names a generic or nested declaration", Assert.Single(Peers()));
    }

    [Theory]
    [InlineData("", "Cluckwork.Temp.Finance.IExpenseRepository", "owner 'Finance' declares a seam but no contract")]
    [InlineData("Cluckwork.Temp.Finance.IExpenseRepository", "Cluckwork.Temp.Finance.IExpenseRepository",
        "owner 'Finance' lists 'Cluckwork.Temp.Finance.IExpenseRepository' in both its contract and its seam")]
    public void SeamShape_IsALoadError(string contract, string seam, string expected)
    {
        var finance = new OwnerDefinition("Finance", "module", ["Cluckwork.Temp.Finance"], [])
        {
            Contract = OneOrNone(contract), Seam = OneOrNone(seam),
        };
        Assert.Contains(ModuleLedger.Validate(new ModuleLedger([finance], [], [])).RegistryErrors,
            e => e.StartsWith(expected, StringComparison.Ordinal));
    }
}
