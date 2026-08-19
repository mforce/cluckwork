namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #546 — the write side's chokepoint. Reads are defended by 27 fail-closed
// query filters; before this slice writes were defended by convention.
//
// Every test drives a REAL AppDbContext through a resolved TenantContext, so
// the interceptor runs exactly as it does in a request.
[Collection(IntegrationCollection.Name)]
public sealed class TenantWriteGuardTests(CluckworkWebApplicationFactory factory)
{
    private static DailyEntry NewEntry(Guid accountId) =>
        DailyEntry.Create(Guid.NewGuid(), accountId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.UtcNow.Date));

    // --- Added -------------------------------------------------------------

    [Fact]
    public async Task Added_ForeignAccountId_Throws()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        var ex = await Assert.ThrowsAsync<TenantWriteMismatchException>(() =>
            factory.WithTenantScopeAsync(accountA, async db =>
            {
                db.DailyEntries.Add(NewEntry(accountB));
                await db.SaveChangesAsync();
            }));

        Assert.Equal(accountA, ex.ExpectedAccountId);
        Assert.Equal(accountB, ex.ActualAccountId);
    }

    [Fact]
    public async Task Added_EmptyAccountId_IsStamped()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");

        var id = await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = NewEntry(Guid.Empty);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var stamped = await factory.WithTenantScopeAsync(accountA, db =>
            db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == id));

        Assert.Equal(accountA, stamped.AccountId);
    }

    [Fact]
    public async Task Added_MatchingAccountId_IsAllowed()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");

        var id = await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = NewEntry(accountA);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var saved = await factory.WithTenantScopeAsync(accountA, db =>
            db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == id));

        Assert.Equal(accountA, saved.AccountId);
    }

    // --- Modified ----------------------------------------------------------

    [Fact]
    public async Task Modified_ForeignAccountId_Throws()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        var id = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(accountB);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        // Serving A, reach past the filter for B's row and edit it.
        var ex = await Assert.ThrowsAsync<TenantWriteMismatchException>(() =>
            factory.WithTenantScopeAsync(accountA, async db =>
            {
                var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
                entry.RecordProduction(100, 1, 1, 0, 0);
                await db.SaveChangesAsync();
            }));

        Assert.Equal(accountA, ex.ExpectedAccountId);
        Assert.Equal(accountB, ex.ActualAccountId);
    }

    [Fact]
    public async Task Modified_AccountIdRewrittenToCurrentTenant_Throws()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        var id = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(accountB);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        // Theft, not a leak: B's row RELABELLED as A's. The current value now
        // equals the tenant, so only the ORIGINAL value can catch this.
        await Assert.ThrowsAsync<TenantWriteMismatchException>(() =>
            factory.WithTenantScopeAsync(accountA, async db =>
            {
                var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
                db.Entry(entry).Property(nameof(DailyEntry.AccountId)).CurrentValue = accountA;
                await db.SaveChangesAsync();
            }));
    }

    // The MIRROR of the theft case above, and the only test that pins the
    // CURRENT-value half of the Modified check. Here the row is legitimately
    // ours (OriginalValue == tenant, so the original-value check is satisfied)
    // and the write pushes it OUT to another account. Without
    // Verify(prop.CurrentValue) this is written silently — a tenant can donate
    // its own row into someone else's farm.
    [Fact]
    public async Task Modified_AccountIdRewrittenToForeignTenant_Throws()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        var id = await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = NewEntry(accountA);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var ex = await Assert.ThrowsAsync<TenantWriteMismatchException>(() =>
            factory.WithTenantScopeAsync(accountA, async db =>
            {
                var entry = await db.DailyEntries.SingleAsync(e => e.Id == id);
                db.Entry(entry).Property(nameof(DailyEntry.AccountId)).CurrentValue = accountB;
                await db.SaveChangesAsync();
            }));

        Assert.Equal(accountA, ex.ExpectedAccountId);
        Assert.Equal(accountB, ex.ActualAccountId);
    }

    // --- Deleted -----------------------------------------------------------

    [Fact]
    public async Task Deleted_ForeignAccountId_Throws()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        var id = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(accountB);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var ex = await Assert.ThrowsAsync<TenantWriteMismatchException>(() =>
            factory.WithTenantScopeAsync(accountA, async db =>
            {
                var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
                db.DailyEntries.Remove(entry);
                await db.SaveChangesAsync();
            }));

        Assert.Equal(accountB, ex.ActualAccountId);
    }

    // --- Unresolved tenant -------------------------------------------------
    //
    // These two pin an ABSENCE of change. Nothing else in the suite goes red if
    // a later edit makes the unresolved path fail closed, and the seeders and
    // CLI verbs would then break far from the cause. One test per CALLER, not
    // one for both, so the failure names who it broke.

    [Fact]
    public async Task UnresolvedTenant_PermitsSeederWrite()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        await AssertUnresolvedWriteSucceedsAsync(accountA);
    }

    [Fact]
    public async Task UnresolvedTenant_PermitsCliVerbWrite()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        await AssertUnresolvedWriteSucceedsAsync(accountA);
    }

    private async Task AssertUnresolvedWriteSucceedsAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        Assert.False(tenant.IsResolved);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = NewEntry(accountId);
        db.DailyEntries.Add(entry);
        await db.SaveChangesAsync();

        var saved = await factory.WithTenantScopeAsync(accountId, d =>
            d.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == entry.Id));
        Assert.Equal(accountId, saved.AccountId);
    }
}
