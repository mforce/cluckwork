namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Expenses;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #259 — idempotency_records grows without bound: IdempotencyMiddleware inserts a
// claim per idempotent write and marks it Completed with the replay payload, but
// nothing ever deletes an old row. IdempotencyRecordPurgeSweep (a DurableJobWorker
// sweep modeled on RefreshTokenPurgeSweep) purges rows whose CreatedAt is older
// than IdempotencyRecordPurgeSweep.PurgeRetention, across every account in one
// global batched delete (the table is not tenant-query-filtered).
[Collection(IntegrationCollection.Name)]
public sealed class IdempotencyRecordPurgeSweepTests(CluckworkWebApplicationFactory factory)
{
    private static IdempotencyRecord NewRecord(
        DateTimeOffset createdAt,
        IdempotencyStatus status = IdempotencyStatus.Completed,
        Guid? accountId = null,
        DateTimeOffset? leaseExpiresAt = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId ?? Guid.NewGuid(),
        // 64 hex chars each — matches the real SHA-256 hex shape and stays unique
        // across rows in one test (the (AccountId, EndpointHash, IdempotencyKeyHash)
        // index is unique).
        EndpointHash = Hash(),
        IdempotencyKeyHash = Hash(),
        RequestHash = Hash(),
        Status = status,
        LeaseOwner = Guid.NewGuid(),
        LeaseExpiresAt = leaseExpiresAt ?? createdAt.AddSeconds(30),
        StatusCode = status == IdempotencyStatus.Completed ? 201 : null,
        ContentType = status == IdempotencyStatus.Completed ? "application/json" : null,
        ResponseBody = status == IdempotencyStatus.Completed ? "{\"id\":\"x\"}" : null,
        CompletedAt = status == IdempotencyStatus.Completed ? createdAt : null,
        CreatedAt = createdAt,
    };

    private static string Hash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    // idempotency_records has no tenant query filter, so seed/read with a throwaway
    // tenant scope (any AccountId works — the sweep never scopes).
    private Task SeedAsync(params IdempotencyRecord[] records) =>
        factory.WithTenantScopeAsync(Guid.NewGuid(), async db =>
        {
            db.IdempotencyRecords.AddRange(records);
            await db.SaveChangesAsync();
        });

    private Task RunSweepAsync() =>
        factory.Services.GetRequiredService<IdempotencyRecordPurgeSweep>().RunAsync(CancellationToken.None);

    private Task<bool> ExistsAsync(Guid id) =>
        factory.WithTenantScopeAsync(Guid.NewGuid(),
            db => db.IdempotencyRecords.IgnoreQueryFilters().AnyAsync(r => r.Id == id));

    [Fact]
    public async Task Sweep_CollectsAgedRows_BothCompletedAndInProgress()
    {
        var now = DateTimeOffset.UtcNow;
        var agedCompleted = NewRecord(
            now - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1));
        // An aged InProgress row whose lease is ALSO expired (the default lease
        // here is createdAt+30s, long past) is an abandoned claim, so it is
        // collected too — not just Completed rows.
        var agedAbandoned = NewRecord(
            now - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1),
            IdempotencyStatus.InProgress);
        await SeedAsync(agedCompleted, agedAbandoned);

        await RunSweepAsync();

        Assert.False(await ExistsAsync(agedCompleted.Id));
        Assert.False(await ExistsAsync(agedAbandoned.Id));
    }

    // #421 codex review — an aged InProgress row is NOT necessarily abandoned:
    // the steal path renews LeaseExpiresAt on the existing row without touching
    // CreatedAt, so a retry can hold a live lease over a claim created >48h ago.
    // Purging it mid-flight would race the handler's guarded publish (0 rows
    // updated → business mutation rolled back → 409). The lease guard must retain
    // it until its lease actually expires.
    [Fact]
    public async Task Sweep_LeavesAgedInProgress_WithALiveRenewedLease()
    {
        var now = DateTimeOffset.UtcNow;
        var liveRenewed = NewRecord(
            now - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1),
            IdempotencyStatus.InProgress,
            leaseExpiresAt: now.AddMinutes(5));
        await SeedAsync(liveRenewed);

        await RunSweepAsync();

        Assert.True(await ExistsAsync(liveRenewed.Id));
    }

    // #421 codex review round 3 — the real guard for the renewal/delete race. The
    // row starts aged AND lease-expired, so the sweep's InProgress DELETE selects
    // it; a concurrent steal renews the lease while that DELETE is blocked on the
    // row lock. The unbatched, target-predicated DELETE re-checks LeaseExpiresAt
    // against the renewed row (READ COMMITTED EvalPlanQual) and skips it. The old
    // batched `Id IN (subquery)` form re-checked only the id and would delete it,
    // so reverting to that form turns this test red — which the already-live-lease
    // test above cannot do (that one is excluded at selection time in both forms).
    [Fact]
    public async Task Sweep_DoesNotDeleteAnExpiredClaimStolenWhileTheDeleteIsBlocked()
    {
        var now = DateTimeOffset.UtcNow;
        var stolen = NewRecord(
            now - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1),
            IdempotencyStatus.InProgress); // default lease = createdAt + 30s → expired
        await SeedAsync(stolen);

        // A steal on its own connection: renew the lease into the future and hold
        // the row lock by leaving the transaction open. idempotency_records has no
        // tenant filter, so a raw UPDATE needs no tenant resolution.
        await using var stealScope = factory.Services.CreateAsyncScope();
        var stealDb = stealScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var stealTx = await stealDb.Database.BeginTransactionAsync();
        await stealDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE idempotency_records SET "LeaseExpiresAt" = {now.AddMinutes(5)} WHERE "Id" = {stolen.Id}""");

        // Start the sweep (its own scope/connection). Its InProgress DELETE selects
        // the still-expired row and blocks on the steal's row lock.
        var sweep = RunSweepAsync();
        // Give the DELETE time to reach the blocked state, then let the steal commit
        // the renewal. On the FIXED code the row survives under every interleaving,
        // so this delay only governs whether the broken form would be caught, never
        // whether correct code flakes.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await stealTx.CommitAsync();

        await sweep; // unblocks, re-checks the now-live lease, skips the row

        Assert.True(await ExistsAsync(stolen.Id));
    }

    [Fact]
    public async Task Sweep_LeavesFreshRows_NearAndInsideTheWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var brandNew = NewRecord(now);
        // Old enough that its client retry horizon is long past, but still WITHIN
        // the retention window — the retained side of the boundary.
        var withinWindow = NewRecord(
            now - IdempotencyRecordPurgeSweep.PurgeRetention + TimeSpan.FromHours(1));
        await SeedAsync(brandNew, withinWindow);

        await RunSweepAsync();

        Assert.True(await ExistsAsync(brandNew.Id));
        Assert.True(await ExistsAsync(withinWindow.Id));
    }

    [Fact]
    public async Task Sweep_IsTenantSafeAndIdempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var agedA = NewRecord(
            now - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1), accountId: accountA);
        var freshA = NewRecord(now, accountId: accountA);
        var agedB = NewRecord(
            now - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1), accountId: accountB);
        var freshB = NewRecord(now, accountId: accountB);
        await SeedAsync(agedA, freshA, agedB, freshB);

        await RunSweepAsync();
        await RunSweepAsync(); // idempotent — a second run must not throw or touch survivors

        Assert.False(await ExistsAsync(agedA.Id));
        Assert.False(await ExistsAsync(agedB.Id));
        Assert.True(await ExistsAsync(freshA.Id));
        Assert.True(await ExistsAsync(freshB.Id));
    }

    // The delete is batched (the first sweep after this ships is the big one, and
    // an unbounded ExecuteDelete would take months of backlog in a single
    // statement). Batching is only correct if a backlog larger than one batch
    // still fully drains, so seed across the boundary and prove it.
    [Fact]
    public async Task Sweep_DrainsABacklogLargerThanOneBatch()
    {
        var now = DateTimeOffset.UtcNow;
        var aged = Enumerable.Range(0, IdempotencyRecordPurgeSweep.BatchSizeForTests + 25)
            .Select(_ => NewRecord(
                now - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1)))
            .ToArray();
        var survivor = NewRecord(now);
        await SeedAsync([.. aged, survivor]);

        await RunSweepAsync();

        foreach (var record in aged)
            Assert.False(await ExistsAsync(record.Id), "every aged row should drain, not just the first batch");
        Assert.True(await ExistsAsync(survivor.Id));
    }

    // The load-bearing correctness test: purging a completed claim must not wedge
    // the key. After the row is gone, replaying the same Idempotency-Key must
    // RE-EXECUTE (a fresh claim, a real side effect) rather than 500 on a
    // half-remembered claim or silently replay a response that no longer exists.
    // Uses an expense (append-only, no natural-key uniqueness) so a second row is
    // unambiguous evidence the write ran again.
    [Fact]
    public async Task Sweep_DoesNotWedgeKey_ReplayAfterPurgeReExecutes()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(ExpenseCategory.Create(
                categoryId, accountId, SeedDefaults.FarmId, "Test-Category"));
            await db.SaveChangesAsync();
        });

        var body = new
        {
            expenseCategoryId = categoryId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            description = "Test expense",
            amountMinorUnits = 10_00L,
            flockId = (Guid?)null,
            note = (string?)null,
        };
        var key = Guid.NewGuid().ToString();

        var first = await client.PostWithKeyAsync("/api/v1/expenses", key, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Age the just-created idempotency claim past the retention window, then
        // sweep it away.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            await db.IdempotencyRecords
                .IgnoreQueryFilters()
                .Where(r => r.AccountId == accountId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    r => r.CreatedAt,
                    DateTimeOffset.UtcNow - IdempotencyRecordPurgeSweep.PurgeRetention - TimeSpan.FromHours(1)));
        });
        await RunSweepAsync();

        var afterPurge = await factory.WithTenantScopeAsync(accountId,
            db => db.IdempotencyRecords.IgnoreQueryFilters().AnyAsync(r => r.AccountId == accountId));
        Assert.False(afterPurge); // the claim is gone

        // Same key, same payload — with the claim purged the key is forgotten, so
        // this must run the write again rather than replay or fail.
        var replay = await client.PostWithKeyAsync("/api/v1/expenses", key, body);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);

        var count = await factory.WithTenantScopeAsync(accountId,
            db => db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(2, count); // re-executed: two rows, not one
    }
}
