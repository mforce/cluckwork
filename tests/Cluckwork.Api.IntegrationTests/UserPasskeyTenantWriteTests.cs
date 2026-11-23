using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

// #1284 (G7.2): AspNetUserPasskeys gets #1276's seam, a shadow AccountId that
// TenantStampInterceptor stamps and verifies and the #562 walk tokens, plus the
// composite foreign key (UserId, AccountId) -> AspNetUsers(Id, AccountId).
// These are UserTokenTenantWriteTests' six cases on this table; that file and
// UserRoleTenantWriteTests explain each layer. Each test asserts the refusal
// AND that the target row is untouched.
[Collection(IntegrationCollection.Name)]
public sealed class UserPasskeyTenantWriteTests(CluckworkWebApplicationFactory factory)
{
    private static byte[] NewId() => Guid.NewGuid().ToByteArray();

    private static IdentityUserPasskey<Guid> Row(Guid userId, byte[] credentialId) => new()
    {
        CredentialId = credentialId,
        UserId = userId,
        Data = new IdentityPasskeyData
        {
            PublicKey = [1], Name = "probe", CreatedAt = DateTimeOffset.UnixEpoch,
            AttestationObject = [1], ClientDataJson = [1],
        },
    };

    private static async Task<Exception?> CaptureAsync(Func<Task> write)
    {
        try { await write(); return null; }
        catch (Exception e) { return e; }
    }

    private static bool IsForeignKeyRefusal(Exception? e) =>
        e is DbUpdateException { InnerException: PostgresException { SqlState: "23503" } pg }
        && pg.ConstraintName == "FK_AspNetUserPasskeys_AspNetUsers_UserId_AccountId";

    private static string Describe(Exception? e) =>
        e is null ? "none" : $"{e.GetType().Name} / inner={e.InnerException?.GetType().Name ?? "-"}";

    private sealed record TwoFarms(Guid AccountA, Guid AccountB, Guid UserB);

    // Two farms, one user each, and one passkey row on B's user, written under
    // B's tenant so the interceptor stamps it as production would. Each test
    // seeds its own credential id, since the id is the table's key.
    private byte[] _seeded = [];

    private async Task<TwoFarms> SeedAsync()
    {
        _seeded = NewId();
        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync(emailB);

        var userB = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var id = await db.Users.Where(u => u.Email == emailB).Select(u => u.Id).SingleAsync();
            db.UserPasskeys.Add(Row(id, _seeded));
            await db.SaveChangesAsync();
            return id;
        });
        return new TwoFarms(accountA, accountB, userB);
    }

    private Task<bool> RowExistsAsync(Guid tenant, Guid userId, byte[] credentialId) =>
        factory.WithTenantScopeAsync(tenant, db =>
            db.UserPasskeys.AsNoTracking().AnyAsync(p => p.UserId == userId && p.CredentialId == credentialId));

    [Fact]
    public async Task DetachedAdd_RowForAnotherFarmsUser_IsRefusedByTheForeignKey()
    {
        var f = await SeedAsync();
        var fresh = NewId();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            db.UserPasskeys.Add(Row(f.UserB, fresh));
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, fresh);

        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(row for B's user) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.False(exists, "the cross-farm passkey row was written");
    }

    [Fact]
    public async Task DetachedRemove_StubOfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            db.UserPasskeys.Remove(Row(f.UserB, _seeded));
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, _seeded);

        // By TYPE: the token would also refuse this, as DbUpdateConcurrencyException.
        Assert.True(thrown is TenantWriteMismatchException,
            $"Remove(stub of B's row) was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"B's row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.True(exists, "B's passkey row was deleted");
    }

    [Fact]
    public async Task ForgedDetachedRemove_StubClaimingThisFarm_IsRefusedByTheDatabase()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var stub = Row(f.UserB, _seeded);
            db.UserPasskeys.Attach(stub);
            db.Entry(stub).Property("AccountId").OriginalValue = f.AccountA;
            db.Entry(stub).Property("AccountId").CurrentValue = f.AccountA;
            db.Entry(stub).State = EntityState.Deleted;
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, _seeded);

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"forged Remove(stub claiming A) was not refused by the database: thrown={Describe(thrown)}; " +
            $"B's row exists after={exists}; tenant A={f.AccountA} B={f.AccountB} userB={f.UserB}");
        Assert.True(exists, "B's passkey row was deleted");
    }

    // Under no resolved tenant nothing stamps the column, so the FK refuses the
    // insert even for this farm's own user. The name is new for that user, so
    // only the FK, not the primary key, can refuse it.
    [Fact]
    public async Task Add_UnderNoResolvedTenant_IsRefusedByTheForeignKey()
    {
        var f = await SeedAsync();
        var fresh = NewId();

        var thrown = await CaptureAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            // Deliberately NO tenant.Resolve here.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserPasskeys.Add(Row(f.UserB, fresh));
            await db.SaveChangesAsync();
        });

        var exists = await RowExistsAsync(f.AccountB, f.UserB, fresh);

        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(own farm's user, no resolved tenant) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant B={f.AccountB} userB={f.UserB}");
        Assert.False(exists, "an unowned passkey row was written under no resolved tenant");
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
            var row = await db.UserPasskeys.SingleAsync(p => p.CredentialId == _seeded);
            db.Entry(row).Property("AccountId").CurrentValue = f.AccountA;
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(f.AccountA, db =>
            db.UserPasskeys.AsNoTracking()
                .Where(p => p.CredentialId == _seeded)
                .Select(p => EF.Property<Guid>(p, "AccountId"))
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
            var row = await db.UserPasskeys.SingleAsync(p => p.CredentialId == _seeded);
            db.UserPasskeys.Remove(row);
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, _seeded);

        Assert.True(thrown is TenantWriteMismatchException,
            $"tracked Remove of B's row was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"B's row exists after={exists}; tenant A={f.AccountA} userB={f.UserB}");
        Assert.True(exists, "B's passkey row was deleted");
    }
}
