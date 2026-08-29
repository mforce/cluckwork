namespace Cluckwork.Domain.Tests.Accounts;

using Cluckwork.Domain.Accounts;

// #612 — the one effective-role resolver FlockScopeGuard/Middleware and
// assignment admission all share, so "which roles are Worker-scoped" has one
// answer. Precedence matches AuthPolicies.EffectiveRole (route policies are a
// separate, untouched surface — #612 does not widen or narrow them).
public sealed class RolesTests
{
    [Fact]
    public void ResolveEffective_NoRoles_IsWorker()
    {
        Assert.Equal(EffectiveAccountRole.Worker, Roles.ResolveEffective([]));
    }

    [Theory]
    [InlineData(new[] { Roles.Owner }, EffectiveAccountRole.Owner)]
    [InlineData(new[] { Roles.Manager }, EffectiveAccountRole.Manager)]
    [InlineData(new[] { Roles.Sales }, EffectiveAccountRole.Sales)]
    [InlineData(new[] { Roles.ReadOnly }, EffectiveAccountRole.ReadOnly)]
    public void ResolveEffective_ASingleKnownRole_ResolvesToIt(string[] roles, EffectiveAccountRole expected)
    {
        Assert.Equal(expected, Roles.ResolveEffective(roles));
    }

    [Theory]
    [InlineData(new[] { Roles.ReadOnly, Roles.Owner }, EffectiveAccountRole.Owner)]
    [InlineData(new[] { Roles.ReadOnly, Roles.Sales }, EffectiveAccountRole.Sales)]
    [InlineData(new[] { Roles.Sales, Roles.Manager }, EffectiveAccountRole.Manager)]
    [InlineData(new[] { Roles.Manager, Roles.Owner }, EffectiveAccountRole.Owner)]
    public void ResolveEffective_MultipleKnownRoles_HighestWins(string[] roles, EffectiveAccountRole expected)
    {
        Assert.Equal(expected, Roles.ResolveEffective(roles));
    }

    [Fact]
    public void ResolveEffective_OnlyUnknownRoles_IsDenied()
    {
        Assert.Equal(EffectiveAccountRole.Denied, Roles.ResolveEffective(["Contractor"]));
    }
}
