using Cluckwork.Application.Modules.ContractFixture.Contracts;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class ContractDerivationTests
{
    private static readonly (ILookup<string, string> Types, string[] Errors) Derived =
        RealModuleLedger.DeriveContracts(typeof(ContractDerivationTests).Assembly.GetTypes());

    [Fact]
    public void TopLevelTypesInAContractsNamespace_AreTheOwnersContract() =>
        Assert.Equal(
            [
                typeof(ContractFixtureCommand).FullName!,
                typeof(ContractFixtureMarked).FullName!,
                typeof(ContractFixtureResolution).FullName!,
                typeof(IContractFixtureModule).FullName!,
            ],
            Derived.Types["ContractFixture"]);

    [Fact]
    public void AContractsTypeThatIsAlsoMarked_IsARegistryError() =>
        Assert.Equal(
            [$"contract type '{typeof(ContractFixtureMarked).FullName}' sits in a Contracts namespace and also carries [ModuleContract]; delete the mark"],
            Derived.Errors);
}
