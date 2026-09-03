namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

// #562 — the write guard's provenance gap, closed at the database.
//
// TenantStampInterceptor compares AccountId's ORIGINAL value against the
// resolved tenant, and that value is the database's only for an entity that
// was LOADED while tracked. DbSet.Update, DbSet.Remove and Attach seed the
// original values from the caller's own instance, so a hand-built stub
// carrying another farm's primary key and THIS farm's AccountId passed both
// halves of the check and the UPDATE/DELETE keyed on the primary key alone.
//
// Since #562 AccountId is a concurrency token on every entity that carries one
// (AppDbContext.OnModelCreating), so the statement the database runs is
// "WHERE Id = @id AND AccountId = @original" — the stub's original is the
// tenant's, the row's is not, zero rows match, and EF throws
// DbUpdateConcurrencyException. The interceptor never sees a difference; the
// database does. Three shapes, each of which was observed WRITING THROUGH on
// the unmodified tree before the fix:
//
//   * Update(stub)  — B's row relabelled to A (theft, not a leak);
//   * Remove(stub)  — B's row deleted;
//   * Attach(stub) as Unchanged + edit ONLY the owned Money — B's row's cost
//     rewritten. The interceptor cannot see this one at all: the principal is
//     Unchanged and the owned entry has no AccountId of its own.
//
// Each test asserts the refusal AND that the row is untouched, because a
// refusal that still mutated the row would pass a throws-only assertion.
[Collection(IntegrationCollection.Name)]
public sealed class DetachedTenantWriteTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private static DailyEntry NewEntry(Guid id, Guid accountId) =>
        DailyEntry.Create(id, accountId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today);

    private static InventoryItem NewItem(Guid id, Guid accountId, long costMinorUnits) =>
        InventoryItem.Create(id, accountId, Guid.NewGuid(), $"Item {id:N}", InventoryCategory.Feed, "kg",
            new Money(costMinorUnits, "USD", 2));

    private async Task<(Guid accountA, Guid accountB)> TwoFarmsAsync()
    {
        var a = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var b = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");
        return (a, b);
    }

    private async Task<Guid> SeedEntryForAsync(Guid accountB)
    {
        return await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountB);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> write)
    {
        try { await write(); return null; }
        catch (Exception e) { return e; }
    }

    [Fact]
    public async Task DetachedUpdate_StubWithForeignKeyAndOwnAccountId_IsRefusedByTheDatabase()
    {
        var (accountA, accountB) = await TwoFarmsAsync();
        var rowId = await SeedEntryForAsync(accountB);

        // B's primary key, A's AccountId, never loaded.
        var stub = NewEntry(rowId, accountA);

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Update(stub);
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(accountA, db =>
            db.DailyEntries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.Id == rowId));

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"Update(stub) was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}; " +
            $"row AccountId after={after?.AccountId.ToString() ?? "ROW GONE"} (was B={accountB}); tenant A={accountA}");
        Assert.NotNull(after);
        Assert.Equal(accountB, after.AccountId);
    }

    [Fact]
    public async Task DetachedRemove_StubWithForeignKeyAndOwnAccountId_IsRefusedByTheDatabase()
    {
        var (accountA, accountB) = await TwoFarmsAsync();
        var rowId = await SeedEntryForAsync(accountB);

        var stub = NewEntry(rowId, accountA);

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Remove(stub);
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(accountA, db =>
            db.DailyEntries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.Id == rowId));

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"Remove(stub) was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}; " +
            $"row after={(after is null ? "DELETED" : "present")} (was B={accountB}); tenant A={accountA}");
        Assert.NotNull(after);
        Assert.Equal(accountB, after.AccountId);
    }

    // The shape the interceptor is blind to: nothing about the principal
    // changes, only its owned Money, and the owned entry carries no AccountId.
    [Fact]
    public async Task OwnedOnlyModification_OnAttachedForeignStub_IsRefusedByTheDatabase()
    {
        var (accountA, accountB) = await TwoFarmsAsync();
        var rowId = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var item = NewItem(Guid.NewGuid(), accountB, 100);
            db.InventoryItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });

        var stub = NewItem(rowId, accountA, 100);
        var states = "";

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.InventoryItems.Attach(stub);
            var owned = db.Entry(stub).Reference(nameof(InventoryItem.DefaultUnitCost)).TargetEntry!;
            owned.Property(nameof(Money.MinorUnits)).CurrentValue = 999L;
            states = $"principal={db.Entry(stub).State} owned={owned.State}";
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(accountA, db =>
            db.InventoryItems.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(i => i.Id == rowId));

        Assert.Equal("principal=Unchanged owned=Modified", states);
        Assert.True(thrown is DbUpdateConcurrencyException,
            $"owned-only write was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}; {states}; " +
            $"row cost after={after?.DefaultUnitCost?.MinorUnits.ToString() ?? "ROW GONE"} (was 100, B={accountB}); tenant A={accountA}");
        Assert.NotNull(after);
        Assert.Equal(accountB, after.AccountId);
        Assert.Equal(100, after.DefaultUnitCost!.MinorUnits);
    }
}
