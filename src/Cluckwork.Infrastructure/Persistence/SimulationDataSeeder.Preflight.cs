using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // The three saleable grades SeedFlockHistoryAsync/SeedSalesAsync consume by
    // name (grades["Large"/"Medium"/"Small"]). The #283 migration bakes them
    // in; preflight requires all three present AND saleable so a schema
    // missing one fails loud here instead of throwing KeyNotFoundException
    // mid-seed (#279 review Fix 3).
    private static readonly string[] RequiredSaleableGrades = ["Large", "Medium", "Small"];

    // Existence + shape checks, run BEFORE tenant.Resolve — every tenant-scoped
    // query needs IgnoreQueryFilters (mirrors DemoDataSeeder's own
    // MissingBaseDataAsync). #279 review Fix 3 (codex): each check now PROVES the
    // specific base datum this seeder depends on rather than a weaker proxy —
    // the exact saleable Large/Medium/Small grades (not merely "any grade").
    // #500 (codex round 4) — this checks the MIGRATION-BAKED base data ONLY.
    // The Owner is deliberately no longer folded in: it is a different cause
    // with a different remedy (`bootstrap-admin` provisions an Owner and does
    // nothing whatever for a renamed grade), and one boolean covering both
    // forced the caller to guess which had failed. The caller checks the Owner
    // separately, from the single FindOwnerAsync lookup.
    private async Task<bool> MissingBaseDataAsync(Guid accountId, CancellationToken ct)
    {
        if (!await farmFixture.AccountExistsAsync(accountId, ct)) return true;

        if (!await access.OwnerRoleExistsAsync(ct)) return true;

        // Tenant is unresolved here, so the fixture reads with the filter off
        // and an explicit AccountId (the repository's ListActiveAsync would
        // apply the tenant filter and see nothing).
        var saleableGradeNames = await eggsFixture.ListSaleableGradeNamesAsync(accountId, ct);
        return !RequiredSaleableGrades.All(saleableGradeNames.Contains);
    }

    // #500 — the Owner that signs the fixture (slice 1: all of it).
    //
    // UserManager.GetUsersInRoleAsync is NOT account-scoped — it takes a role
    // name and returns Owners across EVERY account — so the AccountId filter is
    // load-bearing. Ordered by Id so the choice is deterministic when several
    // Owners exist; a fixture whose attribution varied between runs would break
    // the convergence contract (#279). Shared with the preflight above so the
    // two can never disagree about whether an Owner exists.
    //
    // DISABLED Owners are excluded for the same reason DemoDataSeeder excludes
    // them — the fuller note lives there. Disabling keeps the Owner role row and
    // only stamps DisabledAt, so GetUsersInRoleAsync still returns them, and
    // signing the fixture with an account that cannot log in makes every History
    // line name somebody nobody can sign in as.
    //
    // Returns the disabled count too — the caller's advice depends on which
    // cause it is. See DemoDataSeeder for the full reasoning.
    //
    // #500 (codex round 5) — and whether the ROLE ITSELF exists, which is a third
    // distinct state hiding behind `Owner is null`. `GetUsersInRoleAsync` returns
    // an EMPTY LIST for an absent role rather than throwing (that is
    // `AddToRoleAsync`/`IsInRoleAsync`; `UserStore.GetUsersInRoleAsync` returns
    // empty when FindRoleAsync misses), so a schema whose migration-baked roles
    // were dropped is indistinguishable from "nobody has been made an Owner yet"
    // — and the two have completely different remedies. `bootstrap-admin` cannot
    // repair a missing role; only restoring the base data can.
    private async Task<(bool RoleExists, AccessUserSummary? Owner, int DisabledOwners)> FindOwnerAsync(
        Guid accountId, CancellationToken ct)
    {
        if (!await access.OwnerRoleExistsAsync(ct)) return (false, null, 0);

        // #532 — see DemoDataSeeder: scoped at the query, not post-filtered.
        var owners = (await access.ListUsersInRoleAsync(accountId, Roles.Owner, ct)).ToList();

        return (true,
                owners.Where(u => u.DisabledAt is null).OrderBy(u => u.Id).FirstOrDefault(),
                owners.Count(u => u.DisabledAt is not null));
    }
}
