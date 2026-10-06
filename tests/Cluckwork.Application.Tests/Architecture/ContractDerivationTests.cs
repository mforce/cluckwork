using Cluckwork.Application.Modules.ContractFixture.Contracts;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class ContractDerivationTests
{
    private static readonly ILookup<string, string> Derived =
        RealModuleLedger.DeriveContracts(typeof(ContractDerivationTests).Assembly.GetTypes());

    [Fact]
    public void TopLevelTypesInAContractsNamespace_AreTheOwnersContract() =>
        Assert.Equal(
            [
                typeof(ContractFixtureCommand).FullName!,
                typeof(ContractFixtureResolution).FullName!,
                typeof(IContractFixtureModule).FullName!,
            ],
            Derived["ContractFixture"]);
}
