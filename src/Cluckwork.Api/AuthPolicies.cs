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
        // selling the day's eggs IS recording the day's work); ReadOnly-tier
        // users are fenced out.
        opts.AddPolicy(SalesFlow, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
            EffectiveRole(ctx.User) is Roles.Owner or Roles.Manager or Roles.Sales or WorkerRole));

        // Production capture: Owner, Manager, or a plain worker. Sales and
        // ReadOnly exist precisely to fence their holders off production.
        opts.AddPolicy(ProductionWrite, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
            EffectiveRole(ctx.User) is Roles.Owner or Roles.Manager or WorkerRole));
    }

    private const string WorkerRole = "Worker";
    private const string DeniedRole = "Denied";

    // One consistent precedence for principals holding several roles (Identity
    // permits it even though the API assigns one): the HIGHEST role wins —
    // Owner > Manager > Sales > ReadOnly. Without this, negative-role
    // assertions invert (Owner+ReadOnly locked out of the sales flow while
    // Manager+Sales sneaks into production — codex/panel review of #104).
    // A principal carrying ONLY unrecognized role names is denied outright,
    // never treated as a worker: Worker means "no role claims at all".
    private static string EffectiveRole(System.Security.Claims.ClaimsPrincipal user)
    {
        if (user.IsInRole(Roles.Owner)) return Roles.Owner;
        if (user.IsInRole(Roles.Manager)) return Roles.Manager;
        if (user.IsInRole(Roles.Sales)) return Roles.Sales;
        if (user.IsInRole(Roles.ReadOnly)) return Roles.ReadOnly;
        return user.FindAll("role").Any() ? DeniedRole : WorkerRole;
    }
}
