namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// #670 — AspNetUserRoles is live RBAC state, and until this slice it had no
// tenant column: both write-side layers (TenantStampInterceptor, the #562
// concurrency-token walk) select entities by a property NAMED AccountId, and
// IdentityUserRole<Guid> carried none. Serving farm A, a hand-built row naming
// farm B's user was inserted and B's Owner grant deleted with no refusal
// (observed on the unmodified tree, 2026-09-02).
//
// The seam (AppDbContext.OnModelCreating): a SHADOW Guid property AccountId on
// IdentityUserRole<Guid> plus a composite foreign key (UserId, AccountId) →
// AspNetUsers(Id, AccountId). No interceptor or walk change — both reach the
// new column by name:
//
//   * Add    — the interceptor stamps the tenant; the FK then refuses any row
//              whose user is not that farm's (Postgres 23503). Under NO resolved
//              tenant nothing stamps, the value stays Guid.Empty, and the FK
//              refuses the insert — fail closed.
//   * Remove — the walk makes the shadow property a concurrency token, so the
//              DELETE carries AND "AccountId" = <original>. A detached stub
//              carries default(Guid) there (EF does not fetch shadow values on
//              Remove), so the interceptor refuses it first; a FORGED stub whose
//              shadow value claims this farm passes the interceptor and is
//              refused by the database (DbUpdateConcurrencyException).
//
// Each test asserts the refusal AND that the target row is untouched, because
// a refusal that still mutated the row would pass a throws-only assertion.
[Collection(IntegrationCollection.Name)]
public sealed class UserRoleTenantWriteTests(CluckworkWebApplicationFactory factory)
{
    private const string ProbeRole = "UserRoleTenantWriteProbe";

    private static async Task<Exception?> CaptureAsync(Func<Task> write)
    {
        try { await write(); return null; }
        catch (Exception e) { return e; }
    }

    private static bool IsForeignKeyRefusal(Exception? e) =>
        e is DbUpdateException { InnerException: PostgresException { SqlState: "23503" } pg }
        && pg.ConstraintName == "FK_AspNetUserRoles_AspNetUsers_UserId_AccountId";

    private static string Describe(Exception? e) =>
        e is null ? "none" : $"{e.GetType().Name} / inner={e.InnerException?.GetType().Name ?? "-"}";

    private sealed record TwoFarms(Guid AccountA, Guid AccountB, Guid UserB, Guid OwnerRoleId, Guid ProbeRoleId);

    // Two farms, one Owner each; a second role minted on A's user so the Add
    // case names a role B's user does NOT already hold.
    private async Task<TwoFarms> SeedAsync()
    {
        var emailA = $"a-{Guid.NewGuid():N}@test.local";
        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        var accountA = await factory.SeedAccountWithUserAsync(emailA);
        var accountB = await factory.SeedAccountWithUserAsync(emailB);
        await factory.AddRoleAsync(emailA, ProbeRole);

        return await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var userB = await db.Users.IgnoreQueryFilters().Where(u => u.Email == emailB).Select(u => u.Id).SingleAsync();
            var ownerRoleId = await db.Roles.Where(r => r.Name == Cluckwork.Domain.Accounts.Roles.Owner).Select(r => r.Id).SingleAsync();
            var probeRoleId = await db.Roles.Where(r => r.Name == ProbeRole).Select(r => r.Id).SingleAsync();
            return new TwoFarms(accountA, accountB, userB, ownerRoleId, probeRoleId);
        });
    }

    private Task<bool> RowExistsAsync(Guid tenant, Guid userId, Guid roleId) =>
        factory.WithTenantScopeAsync(tenant, db =>
            db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId));

    [Fact]
    public async Task DetachedAdd_RowForAnotherFarmsUser_IsRefusedByTheForeignKey()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = f.UserB, RoleId = f.ProbeRoleId });
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, f.ProbeRoleId);

        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(row for B's user) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.False(exists, "the cross-farm role row was written");
    }

    [Fact]
    public async Task DetachedRemove_StubOfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            db.UserRoles.Remove(new IdentityUserRole<Guid> { UserId = f.UserB, RoleId = f.OwnerRoleId });
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, f.OwnerRoleId);

        // The interceptor, by TYPE: the token would also refuse this (as
        // DbUpdateConcurrencyException), and a throws-only assertion could not
        // tell the layers apart. Mutation M3 relies on this being the type.
        Assert.True(thrown is TenantWriteMismatchException,
            $"Remove(stub of B's Owner row) was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"B's Owner row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.True(exists, "B's Owner role row was deleted");
    }

    [Fact]
    public async Task ForgedDetachedRemove_StubClaimingThisFarm_IsRefusedByTheDatabase()
    {
        var f = await SeedAsync();

        // The interceptor cannot see this one: original and current both equal
        // the resolved tenant. Only the token in the DELETE's WHERE can.
        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var stub = new IdentityUserRole<Guid> { UserId = f.UserB, RoleId = f.OwnerRoleId };
            db.UserRoles.Attach(stub);
            db.Entry(stub).Property("AccountId").OriginalValue = f.AccountA;
            db.Entry(stub).Property("AccountId").CurrentValue = f.AccountA;
            db.Entry(stub).State = EntityState.Deleted;
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, f.OwnerRoleId);

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"forged Remove(stub claiming A) was not refused by the database: thrown={Describe(thrown)}; " +
            $"B's Owner row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.True(exists, "B's Owner role row was deleted");
    }

    // INV-1's fail-closed half: no resolved tenant means nothing stamps the
    // column, so the FK refuses the insert — even for THIS farm's own user.
    // Every production role write runs under a resolved tenant; this pins that
    // a path which forgets to is refused rather than writing an unowned row.
    //
    // A second, role-less user in farm A, so that the ONLY constraint able to
    // refuse the row is the FK: Postgres checks the primary key before its
    // referential triggers run, so an insert for a user who already holds the
    // role would be a 23505 and prove nothing about the FK.
    [Fact]
    public async Task Add_UnderNoResolvedTenant_IsRefusedByTheForeignKey()
    {
        var f = await SeedAsync();
        var emailA2 = $"a2-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(f.AccountA, emailA2, asAdmin: false);
        var userA2 = await factory.WithTenantScopeAsync(f.AccountA, db =>
            db.Users.Where(u => u.Email == emailA2).Select(u => u.Id).SingleAsync());

        var thrown = await CaptureAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            // Deliberately NO tenant.Resolve here.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userA2, RoleId = f.OwnerRoleId });
            await db.SaveChangesAsync();
        });

        var exists = await RowExistsAsync(f.AccountA, userA2, f.OwnerRoleId);

        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(own farm's user, no resolved tenant) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant A={f.AccountA} userA2={userA2}");
        Assert.False(exists, "an unowned role row was written under no resolved tenant");
    }

    // The TRACKED shape, which the detached tests above do not cover, and the
    // cheapest precondition on this table: IdentityUserRole has no query
    // filter (FirstRunStatusService reads it anonymously), so loading another
    // farm's grant tracked is a one-line query under any tenant. For a tracked
    // row the shadow AccountId's ORIGINAL value is the database's, and the
    // interceptor verifies that original on Modified and on Deleted — the arm
    // whose omission is the mistake #546 records — so both writes are refused
    // before any SQL. Pinned per table because the precondition is what makes
    // this table different from every filtered one.
    [Fact]
    public async Task TrackedRelabel_OfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var row = await db.UserRoles.SingleAsync(ur => ur.UserId == f.UserB && ur.RoleId == f.OwnerRoleId);
            db.Entry(row).Property("AccountId").CurrentValue = f.AccountA;
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(f.AccountA, db =>
            db.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == f.UserB && ur.RoleId == f.OwnerRoleId)
                .Select(ur => EF.Property<Guid>(ur, "AccountId"))
                .SingleOrDefaultAsync());

        Assert.True(thrown is TenantWriteMismatchException,
            $"tracked relabel of B's Owner row was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"row AccountId after={after} (was B={f.AccountB}); tenant A={f.AccountA} userB={f.UserB}");
        Assert.Equal(f.AccountB, after);
    }

    [Fact]
    public async Task TrackedRemove_OfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var row = await db.UserRoles.SingleAsync(ur => ur.UserId == f.UserB && ur.RoleId == f.OwnerRoleId);
            db.UserRoles.Remove(row);
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, f.OwnerRoleId);

        Assert.True(thrown is TenantWriteMismatchException,
            $"tracked Remove of B's Owner row was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"B's Owner row exists after={exists}; tenant A={f.AccountA} userB={f.UserB}");
        Assert.True(exists, "B's Owner role row was deleted");
    }
}
