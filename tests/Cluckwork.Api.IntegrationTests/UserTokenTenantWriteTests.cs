using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

// #1276 (G1.1) — AspNetUserTokens gets #670's seam before its first writer,
// TwoFactorCredentials: a shadow AccountId that TenantStampInterceptor stamps
// and verifies and the #562 walk tokens, plus the composite foreign key
// (UserId, AccountId) -> AspNetUsers(Id, AccountId). These are the six
// UserRoleTenantWriteTests cases on this table; that file explains each layer.
// Each test asserts the refusal AND that the target row is untouched.
[Collection(IntegrationCollection.Name)]
public sealed class UserTokenTenantWriteTests(CluckworkWebApplicationFactory factory)
{
    private const string Provider = "Cluckwork";
    private const string SeededName = "UserTokenTenantWriteProbe";

    private static async Task<Exception?> CaptureAsync(Func<Task> write)
    {
        try { await write(); return null; }
        catch (Exception e) { return e; }
    }

    private static bool IsForeignKeyRefusal(Exception? e) =>
        e is DbUpdateException { InnerException: PostgresException { SqlState: "23503" } pg }
        && pg.ConstraintName == "FK_AspNetUserTokens_AspNetUsers_UserId_AccountId";

    private static string Describe(Exception? e) =>
        e is null ? "none" : $"{e.GetType().Name} / inner={e.InnerException?.GetType().Name ?? "-"}";

    private sealed record TwoFarms(Guid AccountA, Guid AccountB, Guid UserB);

    // Two farms, one user each, and one token row on B's user, written under
    // B's tenant so the interceptor stamps it as production would.
    private async Task<TwoFarms> SeedAsync()
    {
        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync(emailB);

        var userB = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var id = await db.Users.Where(u => u.Email == emailB).Select(u => u.Id).SingleAsync();
            db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = id, LoginProvider = Provider, Name = SeededName, Value = "b" });
            await db.SaveChangesAsync();
            return id;
        });
        return new TwoFarms(accountA, accountB, userB);
    }

    private Task<bool> RowExistsAsync(Guid tenant, Guid userId, string name) =>
        factory.WithTenantScopeAsync(tenant, db =>
            db.UserTokens.AsNoTracking().AnyAsync(t => t.UserId == userId && t.LoginProvider == Provider && t.Name == name));

    [Fact]
    public async Task DetachedAdd_RowForAnotherFarmsUser_IsRefusedByTheForeignKey()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = f.UserB, LoginProvider = Provider, Name = "AuthenticatorKey", Value = "a" });
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, "AuthenticatorKey");

        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(row for B's user) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.False(exists, "the cross-farm token row was written");
    }

    [Fact]
    public async Task DetachedRemove_StubOfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            db.UserTokens.Remove(new IdentityUserToken<Guid> { UserId = f.UserB, LoginProvider = Provider, Name = SeededName });
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, SeededName);

        // By TYPE: the token would also refuse this, as DbUpdateConcurrencyException.
        Assert.True(thrown is TenantWriteMismatchException,
            $"Remove(stub of B's row) was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"B's row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.True(exists, "B's token row was deleted");
    }

    [Fact]
    public async Task ForgedDetachedRemove_StubClaimingThisFarm_IsRefusedByTheDatabase()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var stub = new IdentityUserToken<Guid> { UserId = f.UserB, LoginProvider = Provider, Name = SeededName };
            db.UserTokens.Attach(stub);
            db.Entry(stub).Property("AccountId").OriginalValue = f.AccountA;
            db.Entry(stub).Property("AccountId").CurrentValue = f.AccountA;
            db.Entry(stub).State = EntityState.Deleted;
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, SeededName);

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"forged Remove(stub claiming A) was not refused by the database: thrown={Describe(thrown)}; " +
            $"B's row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.True(exists, "B's token row was deleted");
    }

    // Under no resolved tenant nothing stamps the column, so the FK refuses the
    // insert even for this farm's own user. The name is new for that user, so
    // only the FK, not the primary key, can refuse it.
    [Fact]
    public async Task Add_UnderNoResolvedTenant_IsRefusedByTheForeignKey()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            // Deliberately NO tenant.Resolve here.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = f.UserB, LoginProvider = Provider, Name = "AuthenticatorKey", Value = "b" });
            await db.SaveChangesAsync();
        });

        var exists = await RowExistsAsync(f.AccountB, f.UserB, "AuthenticatorKey");

        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(own farm's user, no resolved tenant) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant B={f.AccountB} userB={f.UserB}");
        Assert.False(exists, "an unowned token row was written under no resolved tenant");
    }

    // The tracked shape: the table has no query filter, so another farm's row
    // loads tracked under any tenant, and the interceptor verifies its original
    // AccountId on Modified and on Deleted.
    [Fact]
    public async Task TrackedRelabel_OfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var row = await db.UserTokens.SingleAsync(t => t.UserId == f.UserB && t.Name == SeededName);
            db.Entry(row).Property("AccountId").CurrentValue = f.AccountA;
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(f.AccountA, db =>
            db.UserTokens.AsNoTracking()
                .Where(t => t.UserId == f.UserB && t.Name == SeededName)
                .Select(t => EF.Property<Guid>(t, "AccountId"))
                .SingleOrDefaultAsync());

        Assert.True(thrown is TenantWriteMismatchException,
            $"tracked relabel of B's row was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"row AccountId after={after} (was B={f.AccountB}); tenant A={f.AccountA} userB={f.UserB}");
        Assert.Equal(f.AccountB, after);
    }

    [Fact]
    public async Task TrackedRemove_OfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var row = await db.UserTokens.SingleAsync(t => t.UserId == f.UserB && t.Name == SeededName);
            db.UserTokens.Remove(row);
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, SeededName);

        Assert.True(thrown is TenantWriteMismatchException,
            $"tracked Remove of B's row was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"B's row exists after={exists}; tenant A={f.AccountA} userB={f.UserB}");
        Assert.True(exists, "B's token row was deleted");
    }
}
