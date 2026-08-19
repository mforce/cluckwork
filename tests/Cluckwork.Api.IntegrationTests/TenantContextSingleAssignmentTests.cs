namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Persistence;

// #546 — TenantContext is single-assignment: one DI scope serves exactly one
// account. See TenantContext for why re-pointing a live scope is never
// legitimate.
public sealed class TenantContextSingleAssignmentTests
{
    [Fact]
    public void Resolve_DifferentAccount_Throws()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var tenant = new TenantContext();
        tenant.Resolve(first);

        var ex = Assert.Throws<TenantReassignmentException>(() => tenant.Resolve(second));

        Assert.Equal(first, ex.ResolvedAccountId);
        Assert.Equal(second, ex.AttemptedAccountId);
        // The original resolution survives the refusal — a rejected re-resolve
        // must not leave the scope half-repointed.
        Assert.Equal(first, tenant.AccountId);
    }

    // NOT a proof of single-assignment, and deliberately named so nobody reads
    // it as one: this passes identically against the old plain-setter
    // TenantContext and would survive deleting the feature outright. Its job is
    // the opposite — pinning that we did NOT over-tighten into throwing on a
    // harmless same-account re-resolve, which would make every defensive caller
    // order-dependent.
    [Fact]
    public void Resolve_SameAccountTwice_DoesNotThrow()
    {
        var accountId = Guid.NewGuid();
        var tenant = new TenantContext();

        tenant.Resolve(accountId);
        tenant.Resolve(accountId);

        Assert.Equal(accountId, tenant.AccountId);
        Assert.True(tenant.IsResolved);
    }
}
