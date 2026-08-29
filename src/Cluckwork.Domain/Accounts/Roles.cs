namespace Cluckwork.Domain.Accounts;

// #103 (+#84) — the one place role names live. Spec §5.1 roles shipped this
// phase; Vet/Consultant is deferred until a health module exists to gate.
//
// "Admin" is the OWNER role's stored name — it predates the full model (#73)
// and lives in existing databases and refresh tokens, so the string stays;
// only the concept was renamed. Workers deliberately have NO role row: the
// absence of elevated roles IS the worker (matching #73's seed and every
// existing worker user).
public static class Roles
{
    public const string Owner = "Admin";
    public const string Manager = "Manager";
    public const string Sales = "Sales";
    public const string ReadOnly = "ReadOnly";

    /// <summary>Assignable via user management. Worker = create with no role.</summary>
    public static readonly IReadOnlyList<string> Assignable = [Owner, Manager, Sales, ReadOnly];

    // #612 — the one effective-role resolver flock-scope decisions share
    // (FlockScopeGuard, FlockScopeResolutionMiddleware, assignment admission).
    // Same precedence as AuthPolicies.EffectiveRole (route policies are a
    // separate surface this issue does not touch): highest role wins, no
    // roles at all is Worker, and roles that are ALL unrecognized is Denied —
    // never treated as a Worker.
    public static EffectiveAccountRole ResolveEffective(IEnumerable<string> roles)
    {
        var set = roles as ICollection<string> ?? [.. roles];
        if (set.Count == 0) return EffectiveAccountRole.Worker;
        if (set.Contains(Owner)) return EffectiveAccountRole.Owner;
        if (set.Contains(Manager)) return EffectiveAccountRole.Manager;
        if (set.Contains(Sales)) return EffectiveAccountRole.Sales;
        if (set.Contains(ReadOnly)) return EffectiveAccountRole.ReadOnly;
        return EffectiveAccountRole.Denied;
    }
}

// #612 — only a plain Worker is ever flock-scoped. Owner, Manager, Sales,
// ReadOnly and Denied all bypass assignment rows entirely.
public enum EffectiveAccountRole
{
    Worker,
    ReadOnly,
    Sales,
    Manager,
    Owner,
    Denied,
}
