namespace Cluckwork.Api;

using Cluckwork.Domain.Accounts;

// #103 — the role → capability map (spec §5.1/§5.3), replacing #73's binary
// Admin/other split. One policy per capability tier; endpoint groups pick a
// tier, never a raw role name (#84).
//
// The matrix:
//   Owner    → everything (user management is Owner-only)
//   Manager  → corrective actions, config, money — everything but user mgmt
//   Sales    → customers, orders, payments; no production writes, no expenses
//   Worker   → production recording (a user with NO elevated role), flock-
//              scoped once assignments exist (IFlockScopeGuard)
//   ReadOnly → reads only: stock, production reports, dashboards
public static class AuthPolicies
{
    public const string AdminOnly = "AdminOnly";       // Owner + Manager (historic name kept — #73 gates)
    public const string OwnerOnly = "OwnerOnly";       // user management
    public const string SalesAccess = "SalesAccess";   // Owner/Manager/Sales
    public const string SalesFlow = "SalesFlow";       // everyone but ReadOnly (workers sell — #73 principle)
    public const string ProductionWrite = "ProductionWrite"; // Owner/Manager/Worker(no elevated role)

    // Kept for existing references; prefer Roles.* (#84).
    public const string AdminRole = Roles.Owner;

    public static void AddCluckworkPolicies(this Microsoft.AspNetCore.Authorization.AuthorizationOptions opts)
    {
        // Owner + Manager: the "undo, correct, configure, see money" tier.
        // Every pre-#103 AdminOnly gate now admits Managers too (spec §5.1:
        // Manager = farm operations, inventory, reports).
        opts.AddPolicy(AdminOnly, p => p.RequireRole(Roles.Owner, Roles.Manager));

        opts.AddPolicy(OwnerOnly, p => p.RequireRole(Roles.Owner));

        opts.AddPolicy(SalesAccess, p => p.RequireRole(Roles.Owner, Roles.Manager, Roles.Sales));

        // The draft→confirm order flow stays open to workers (#73 principle:
        // selling the day's eggs IS recording the day's work); only ReadOnly
        // is fenced out.
        opts.AddPolicy(SalesFlow, p => p.RequireAssertion(ctx => !ctx.User.IsInRole(Roles.ReadOnly)));

        // Production writes: Owner, Manager, or a plain worker — a user with
        // no elevated role at all. Sales and ReadOnly are excluded: their
        // roles exist precisely to fence them off production capture.
        opts.AddPolicy(ProductionWrite, p => p.RequireAssertion(ctx =>
            ctx.User.IsInRole(Roles.Owner)
            || ctx.User.IsInRole(Roles.Manager)
            || (!ctx.User.IsInRole(Roles.Sales) && !ctx.User.IsInRole(Roles.ReadOnly))));
    }
}
