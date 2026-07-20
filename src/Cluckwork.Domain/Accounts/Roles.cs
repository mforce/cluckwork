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
}
