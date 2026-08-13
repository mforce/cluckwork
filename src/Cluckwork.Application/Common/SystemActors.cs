namespace Cluckwork.Application.Common;

// #500 — labels for the two audit actors that are deliberately NOT people.
//
// Beside AuditActions for the same reason that file exists: audit-row
// vocabulary lives in one place, not inlined at call sites in two projects.
//
// These exist because IAuditWriter now fails closed on an unresolved actor. A
// CLI verb that genuinely has no signed-in human — `bootstrap-admin` creates
// the very first Owner, `recover-admin` runs at a shell during a lockout — must
// still be able to say who it is, rather than silently falling back to a
// placeholder. Declaring "no person, and here is which non-person" is the whole
// point: the old "(unresolved)" said neither.
public static class SystemActors
{
    /// <summary>First-run provisioning (#283). Author of the first Owner's User.Create row.</summary>
    public const string BootstrapAdmin = "(bootstrap-admin)";

    /// <summary>Offline break-glass recovery (#265). Author of the User.BreakGlassReset row.</summary>
    public const string BreakGlass = "(break-glass)";
}
