namespace Cluckwork.Application.Tests.TenantBypass;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #536 Part 1 — the discovery floor: the guard's banned surface is DERIVED from
// the EF model (walk everything, exclude deliberately — docs/decisions/407-writing-a-guard.md),
// never hand-recalled. This test pins the discovered filter-free set so a model
// change that silently adds (or loses) a filter-free entity fails LOUDLY here
// instead of vacuously changing the guard's coverage.
//
// API note: IReadOnlyEntityType.GetQueryFilter() is [Obsolete] in the resolved
// EF Core 10.0.11 ("Use GetDeclaredQueryFilters() instead") — under
// TreatWarningsAsErrors the old call is a build error, so the guard uses
// GetDeclaredQueryFilters(). For plain HasQueryFilter(…) the declared set is
// non-empty iff a filter exists; the equivalence probe (Task 1 Step 2b) proves
// it against this model before the pin below is trusted.
//
// The four Identity claim/login/token tables carry NO AccountId column, so the
// AccountId-predicate rule cannot apply to them: ANY query against them is a
// bypass occurrence requiring an allow-list entry, full stop (review finding
// F7). They are part of the pinned set on purpose.
public sealed class TenantBypassDiscoveryTests
{
    private static Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            .Options;

    [Fact]
    public void DiscoveredSurface_Floor()
    {
        // Model-only construction: no connection is opened by building the model.
        using var db = new AppDbContext(BuildOptions(), new TenantContext());
        var model = db.Model;

        // Deliberate exclusion of a known non-tenant table (see policy above);
        // everything else in the filter-free set is banned surface.
        // Deliberate exclusions: DurableJob (no tenant by design, #271) and
        // Money (owned value types — see policy above; no DbSet exists for them).
        var excludedFromBan = new HashSet<string> { "DurableJob", "Money" };
        var filterFree = model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Where(e => !excludedFromBan.Contains(e.ClrType.Name))
            .Select(e => e.ClrType.Name)
            .OrderBy(n => n)
            .ToList();

        // The pinned filter-free surface. Verified against the model 2026-08-22.
        // A different actual set is a FINDING to report, not a silent re-pin.
        //
        // Exclusion policy for the ban (stated, per review F7):
        //  * DurableJob — a job-scheduling table with no AccountId by design
        //    (at-most-one-leader lease; #271). Never tenant-scoped; the ban
        //    would be a false positive, so it is excluded from the walked
        //    surface.
        //  * IdempotencyRecord — carries an AccountId column but has NO query
        //    filter (the idempotency protocol keys on the request id). Any
        //    query against it is a bypass occurrence: allow-list entry
        //    required, plus the AccountId-predicate rule where a predicate
        //    exists.
        //  * The Identity tables (User/Role + claim/login/token/userrole)
        //    carry no AccountId column on their base types: any query is a
        //    bypass occurrence, allow-list entry required, full stop.
        //  * `Money` appears in the raw model as OWNED value-type entities
        //    (one per owning table: FeedUsages, InventoryItems, InventoryLots,
        //    SalesOrderItems, SalesOrders). Owned types have no DbSet and no
        //    independent query — they are reachable only through their filtered
        //    owner — so they cannot be a direct bypass. Excluded from the pin;
        //    the scanner's db.<Set> leg can never name them, which is correct.
        var expected = new[]
        {
            nameof(ApplicationRole),
            nameof(ApplicationUser),
            "IdempotencyRecord",
            "IdentityRoleClaim`1",
            "IdentityUserClaim`1",
            "IdentityUserLogin`1",
            "IdentityUserRole`1",
            "IdentityUserToken`1",
            "RefreshToken",
        };

        Assert.Equal(expected, filterFree);
    }

    // Task 1 Step 2b probe: equivalence of GetDeclaredQueryFilters() with the
    // pre-10 GetQueryFilter() semantics for plain HasQueryFilter — a filtered
    // entity (Account) must show a declared filter; a filter-free one
    // (ApplicationUser) must not.
    [Fact]
    public void DeclaredQueryFilter_Probe_FiltersAreDeclared()
    {
        using var db = new AppDbContext(BuildOptions(), new TenantContext());
        var model = db.Model;

        var account = model.FindEntityType(typeof(Account));
        var user = model.FindEntityType(typeof(ApplicationUser));

        Assert.NotNull(account);
        Assert.NotNull(user);
        Assert.NotEmpty(account!.GetDeclaredQueryFilters());
        Assert.Empty(user!.GetDeclaredQueryFilters());
    }
}
