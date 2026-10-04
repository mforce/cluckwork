namespace Cluckwork.Application.Tests.Architecture;

// #1023: the peer rule on a temp tree, one named assertion per allowance and registry error.
public sealed class PeerContractTests : IDisposable
{
    private const string FarmSeam = "Cluckwork.Temp.Farm.IAccountRepository";
    private const string AccessClaim = "Cluckwork.Temp.Common.IIdentity";
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
            public interface IIdentity { }
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

    private static ModuleLedger Ledger(string farmSeam, string accessTypes, string flockContract, string financeTypes = "") =>
        ModuleLedger.Validate(new ModuleLedger(
            [
                new("Hub", "platform", ["Cluckwork.Temp", "Cluckwork.Temp.Flocks.Shared"], []),
                new("Farm", "module", ["Cluckwork.Temp.Farm"], [])
                {
                    Contract = ["Cluckwork.Temp.Farm.IFarmModule"], Seam = OneOrNone(farmSeam),
                },
                new("FlockManagement", "module", ["Cluckwork.Temp.Flocks"], []) { Contract = OneOrNone(flockContract) },
                new("Finance", "module", ["Cluckwork.Temp.Finance"], []) { Types = OneOrNone(financeTypes) },
                new("Access", "module", ["Cluckwork.Temp.Access"], [])
                {
                    Contract = ["Cluckwork.Temp.Access.IAccessModule"], Types = OneOrNone(accessTypes),
                },
            ],
            [], [])
        {
            AdapterRoots = new(["Cluckwork.Temp.Endpoints"], [])
            {
                PersistenceForbiddenNamespaces = ["Cluckwork.Temp.Endpoints"],
            },
        });

    private IReadOnlyList<string> Peers(
        string farmSeam = FarmSeam, string accessTypes = AccessClaim,
        string flockContract = FlockContract) =>
        AdapterReachScanner.Evaluate(AdapterReachScanner.ScanPeers(
            Path.Combine(_tempRoot, "src"), Ledger(farmSeam, accessTypes, flockContract)));

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

        var report = AdapterReachScanner.Scan(Path.Combine(_tempRoot, "src"), Ledger(FarmSeam, AccessClaim, FlockContract));
        Assert.Equal("Cluckwork.Temp.Farm.IAccountRepository", Assert.Single(report.ContractBypasses).Type);
    }

    [Fact]
    public void ClaimedType_BelongsToItsClaimingOwner()
    {
        WriteSource("Expense.cs", """
            using Cluckwork.Temp.Common;
            namespace Cluckwork.Temp.Finance;
            public class ExpenseHandler(IIdentity identity) { }
            """);

        Assert.Contains("ExpenseHandler.ctor -> Access through Cluckwork.Temp.Common.IIdentity at src/Expense.cs:3",
            Assert.Single(Peers()));
        Assert.Empty(Peers(accessTypes: ""));
    }

    [Fact]
    public void ClaimedType_IsWalkedAsAMemberOfItsOwner()
    {
        WriteSource("Common.cs", """
            namespace Cluckwork.Temp.Common;
            public interface IIdentity { void Run(Cluckwork.Temp.Flocks.IFlockRepository flocks); }
            public interface IClock { }
            """);

        Assert.Contains("contract bypass Cluckwork.Temp.Common.IIdentity.Run -> FlockManagement", Assert.Single(Peers()));
        Assert.Empty(Peers(accessTypes: ""));
    }

    [Theory]
    [InlineData("seam", "Cluckwork.Temp.Farm.IGone", "owner 'Farm' seam type 'Cluckwork.Temp.Farm.IGone' is not declared under src/")]
    [InlineData("seam", "Cluckwork.Temp.Flocks.IFlockRepository", "owner 'Farm' seam type 'Cluckwork.Temp.Flocks.IFlockRepository' is not in a namespace 'Farm' owns")]
    [InlineData("types", "Cluckwork.Temp.Common.IGone", "owner 'Access' claims type 'Cluckwork.Temp.Common.IGone', which is not declared under src/")]
    [InlineData("types", "Cluckwork.Temp.Flocks.IFlockRepository", "owner 'Access' claims type 'Cluckwork.Temp.Flocks.IFlockRepository' outside a platform namespace")]
    public void BadSeamOrClaimedType_IsARegistryError(string list, string entry, string expected)
    {
        var failure = Assert.Single(list == "seam" ? Peers(farmSeam: entry) : Peers(accessTypes: entry));
        Assert.Contains(expected, failure);
    }

    [Theory]
    [InlineData("contract", "Farm.cs", "Cluckwork.Temp.Farm", "IFarmModule")]
    [InlineData("seam", "Farm.cs", "Cluckwork.Temp.Farm", "IAccountRepository")]
    public void GenericHomonymOfAnEntry_IsARegistryError(string list, string file, string ns, string type)
    {
        WriteSource("Homonym" + file, $"namespace {ns}; public interface {type}<T> {{ }}");

        Assert.Contains($"owner 'Farm' {list} type '{ns}.{type}' names a generic or nested declaration", Assert.Single(Peers()));
    }

    [Theory]
    [InlineData("public interface IIdentity<T> { }", AccessClaim, "names a generic or nested declaration")]
    [InlineData("public class Outer { public interface IIdentity { } }", "Cluckwork.Temp.Common.Outer.IIdentity", "names a generic or nested declaration")]
    [InlineData("public class IIdentity { public class Reader { } }", AccessClaim, "declares nested types")]
    public void GenericOrNestedClaim_IsARegistryError(string declaration, string claim, string expected)
    {
        WriteSource("Common.cs", "namespace Cluckwork.Temp.Common; public interface IClock { } " + declaration);

        Assert.Contains(expected, Assert.Single(Peers(accessTypes: claim)));
    }

    [Fact]
    public void TypeClaimedTwice_IsARegistryError()
    {
        var report = AdapterReachScanner.ScanPeers(Path.Combine(_tempRoot, "src"),
            Ledger(FarmSeam, AccessClaim, FlockContract, financeTypes: AccessClaim));
        Assert.Contains(report.RegistryErrors, e => e.StartsWith("type 'Cluckwork.Temp.Common.IIdentity' is claimed 2 times (Access, Finance)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("module", "", "Cluckwork.Temp.Finance.IExpenseRepository", "",
        "owner 'Finance' declares a seam but no contract")]
    [InlineData("module", "Cluckwork.Temp.Finance.IExpenseRepository", "Cluckwork.Temp.Finance.IExpenseRepository", "",
        "owner 'Finance' lists 'Cluckwork.Temp.Finance.IExpenseRepository' in both its contract and its seam")]
    [InlineData("platform", "", "", "Cluckwork.Temp.Common.IClock",
        "owner 'Finance' is a platform owner and cannot claim types")]
    public void SeamOrClaimShape_IsALoadError(string kind, string contract, string seam, string types, string expected)
    {
        var finance = new OwnerDefinition("Finance", kind, ["Cluckwork.Temp.Finance"], [])
        {
            Contract = OneOrNone(contract), Seam = OneOrNone(seam), Types = OneOrNone(types),
        };
        Assert.Contains(ModuleLedger.Validate(new ModuleLedger([finance], [], [])).RegistryErrors,
            e => e.StartsWith(expected, StringComparison.Ordinal));
    }
}
