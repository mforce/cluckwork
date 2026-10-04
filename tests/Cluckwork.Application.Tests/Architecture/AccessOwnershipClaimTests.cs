namespace Cluckwork.Application.Tests.Architecture;

public sealed class AccessOwnershipClaimTests
{
    [Theory]
    [InlineData("Cluckwork.Application.Common.IIdentityProvider")]
    [InlineData("Cluckwork.Application.Common.IStepUpGrantService")]
    public void CommonIdentityPortRemainsOwnedByContractedAccess(string type)
    {
        var ledger = ModuleLedger.Load(Path.Combine(AppContext.BaseDirectory,
            "Architecture", "Data", "module-ledger.json"));
        var access = Assert.Single(ledger.Owners, owner => owner.Name == "Access");
        Assert.NotEmpty(access.Contract);
        Assert.Contains(type, access.Types);
    }
}
