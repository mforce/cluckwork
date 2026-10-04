namespace Cluckwork.Application.Tests.Architecture;

public sealed class AccessOwnershipClaimTests
{
    [Theory]
    [InlineData("Cluckwork.Application.Common.IIdentityProvider")]
    [InlineData("Cluckwork.Application.Common.IStepUpGrantService")]
    public void CommonIdentityPortRemainsOwnedByContractedAccess(string type)
    {
        var access = Assert.Single(RealModuleLedger.Value.Owners, owner => owner.Name == "Access");
        Assert.NotEmpty(access.Contract);
        Assert.Contains(type, access.Types);
    }
}
