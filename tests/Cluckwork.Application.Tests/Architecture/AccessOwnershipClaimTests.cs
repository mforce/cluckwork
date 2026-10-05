namespace Cluckwork.Application.Tests.Architecture;

public sealed class AccessOwnershipClaimTests
{
    [Theory]
    [InlineData("Cluckwork.Application.Modules.Access.Users.IIdentityProvider")]
    [InlineData("Cluckwork.Application.Modules.Access.Users.IStepUpGrantService")]
    public void CommonIdentityPortRemainsOwnedByContractedAccess(string type)
    {
        var access = Assert.Single(RealModuleLedger.Value.Owners, owner => owner.Name == "Access");
        Assert.NotEmpty(access.Contract);
        Assert.Contains(type, access.Types);
    }
}
