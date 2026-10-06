using Cluckwork.Application.Modules.Access.Users;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class AccessOwnershipClaimTests
{
    // The two Identity ports left Application.Common for Access's own namespace (#1087), so Access owns them
    // through a namespace claim, not a type claim.
    [Theory]
    [InlineData(typeof(IIdentityProvider))]
    [InlineData(typeof(IStepUpGrantService))]
    public void IdentityPortIsOwnedByContractedAccessThroughItsNamespace(Type type)
    {
        var ledger = RealModuleLedger.Value;
        var access = Assert.Single(ledger.Owners, owner => owner.Name == "Access");
        Assert.NotEmpty(access.Contract);

        var errors = new List<string>();
        var index = ModuleLedgerScanner.BuildNamespaceIndex(ledger, errors);
        Assert.Empty(errors);
        var owner = ModuleLedgerScanner.Resolve(index, type.FullName!, declared: true);
        Assert.NotNull(owner);
        Assert.Equal("Access", owner.Value.Owner);
        Assert.True(index[owner.Value.Namespace].Subtree, $"{type.FullName} must be owned through a namespace");
    }
}
