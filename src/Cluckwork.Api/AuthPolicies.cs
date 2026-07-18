namespace Cluckwork.Api;

// #73 — the only role distinction in Phase 1.1: Admin vs everyone else.
// Anything that undoes, corrects, or reconfigures requires Admin; recording
// the day's work does not. House/flock-scoped RBAC is a later slice.
public static class AuthPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string AdminRole = "Admin";
}
