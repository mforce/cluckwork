namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

// #307 — the database-coordinated claim/lease idempotency protocol. Covers
// what IdempotencyReplayTests / IdempotencyUserScopeTests don't:
//   - a request-hash conflict on key reuse (never invokes the handler);
//   - an abandoned (expired-lease) claim's bounded recovery via steal;
//   - a LIVE (unexpired) competing claim's bounded give-up, never invoking
//     the handler a second time;
//   - tenant scoping of the claim lookup itself (not just the cached body).
[Collection(IntegrationCollection.Name)]
public sealed class AtomicIdempotencyProtocolTests(CluckworkWebApplicationFactory factory)
{
    private static object ExpenseBody(Guid categoryId, string description) => new
    {
        expenseCategoryId = categoryId,
        date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        description,
        amountMinorUnits = 5_00L,
        flockId = (Guid?)null,
        note = (string?)null
    };

    private sealed record TenantFixture(Guid AccountId, Guid CategoryId, HttpClient Client);

    private async Task<TenantFixture> SeedAccountWithExpenseCategoryAsync(string emailPrefix)
    {
        var email = $"{emailPrefix}-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(Cluckwork.Domain.Expenses.ExpenseCategory.Create(
                categoryId, accountId, Cluckwork.Domain.Accounts.SeedDefaults.FarmId,
                "Test-Category"));
            await db.SaveChangesAsync();
        });
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return new TenantFixture(accountId, categoryId, client);
    }

    // Acceptance criterion 3: a same-key, different-payload retry is a
    // conflict that NEVER invokes the handler — proven here by an unchanged
    // side-effect count, not just the status code.
    [Fact]
    public async Task DifferentRequestHash_SameKey_ReturnsConflict_AndNeverInvokesHandler()
    {
        var (accountId, categoryId, client) = await SeedAccountWithExpenseCategoryAsync("hash");

        var key = Guid.NewGuid().ToString();
        var first = await client.PostWithKeyAsync(
            "/api/v1/expenses", key, ExpenseBody(categoryId, "Original payload"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var conflicted = await client.PostWithKeyAsync(
            "/api/v1/expenses", key, ExpenseBody(categoryId, "A DIFFERENT payload under the same key"));
        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);

        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(1, count);
    }

    // Acceptance criterion 4: an abandoned in-progress claim (an expired
    // lease with no completion — the "claimant died mid-request" shape) has
    // a bounded recovery path. We drive a real request to establish a row
    // under the middleware's OWN hash, then doctor just the lease fields
    // back to InProgress+expired (never fabricating a hash ourselves), then
    // send the SAME key+body again and confirm it is stolen and re-executed.
    [Fact]
    public async Task AbandonedClaim_ExpiredLease_IsStolenAndRecovers()
    {
        var (accountId, categoryId, client) = await SeedAccountWithExpenseCategoryAsync("abandon");

        var key = Guid.NewGuid().ToString();
        var body = ExpenseBody(categoryId, "Abandoned-claim probe");

        var first = await client.PostWithKeyAsync("/api/v1/expenses", key, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var record = await db.IdempotencyRecords.SingleAsync(r => r.AccountId == accountId);
            record.Status = IdempotencyStatus.InProgress;
            record.LeaseOwner = Guid.NewGuid();
            record.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5); // abandoned well in the past
            record.StatusCode = null;
            record.ContentType = null;
            record.ResponseBody = null;
            record.CompletedAt = null;
            await db.SaveChangesAsync();
        });

        var sw = Stopwatch.StartNew();
        var recovered = await client.PostWithKeyAsync("/api/v1/expenses", key, body);
        sw.Stop();

        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        // Bounded: an expired lease is stolen on first sight, not after
        // waiting out the default 30s lease/max-wait — this must return in
        // low single-digit seconds even under CI load.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"recovery took {sw.Elapsed}, expected near-immediate steal of an already-expired lease");

        // The steal actually re-executed the handler (a second row) — proof
        // this path recovers by running the operation again, not by
        // silently replaying a phantom cached response.
        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(2, count);
    }

    // Tenant scoping of the CLAIM lookup itself (not just the cached body):
    // two different accounts using the IDENTICAL literal Idempotency-Key on
    // the identical endpoint, with payloads that would even conflict if
    // scoping ever leaked, must be completely independent — account B must
    // never learn account A used the same key (no conflict, no replay).
    [Fact]
    public async Task DifferentAccounts_SameLiteralKey_AreFullyIndependent()
    {
        var (accountA, categoryA, clientA) = await SeedAccountWithExpenseCategoryAsync("tenA");
        var (accountB, categoryB, clientB) = await SeedAccountWithExpenseCategoryAsync("tenB");

        // Deliberately the SAME literal key AND the SAME description (so the
        // request hash matches too) — the strongest version of the leak this
        // guards: if the claim/replay lookup ever dropped its AccountId
        // predicate, B's request would find A's (cross-tenant) COMPLETED row
        // and silently REPLAY it — same 201 status, but B's OWN account would
        // never get its own Expense row. A's request completes first
        // (sequential, not concurrent) so there is a real completed row for
        // B's lookup to wrongly find if scoping is broken.
        var sharedKey = Guid.NewGuid().ToString();
        var responseA = await clientA.PostWithKeyAsync(
            "/api/v1/expenses", sharedKey, ExpenseBody(categoryA, "Identical description"));
        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);

        var responseB = await clientB.PostWithKeyAsync(
            "/api/v1/expenses", sharedKey, ExpenseBody(categoryB, "Identical description"));
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);

        var countA = await factory.WithTenantScopeAsync(accountA, db => db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryA));
        var countB = await factory.WithTenantScopeAsync(accountB, db => db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryB));
        Assert.Equal(1, countA);
        // The load-bearing assertion: B independently EXECUTED (its own row
        // exists), rather than replaying A's cross-tenant completed claim.
        Assert.Equal(1, countB);
    }
}

// A live (never-expiring-in-time) competing claim must give up boundedly —
// never hang, never invoke the handler a second time. Needs tiny lease/wait
// settings, so this runs against its own dedicated factory/Postgres rather
// than the shared IntegrationCollection fixture (whose defaults are the real
// 30s production values — deliberately not shrunk repo-wide just to make one
// test fast).
public sealed class FastIdempotencyLeaseFactory : CluckworkWebApplicationFactory
{
    public const int MaxWaitSeconds = 1;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Idempotency:LeaseDurationSeconds", "60");
        builder.UseSetting("Idempotency:MaxWaitSeconds", MaxWaitSeconds.ToString());
    }
}

public sealed class IdempotencyBoundedWaitTests(FastIdempotencyLeaseFactory factory)
    : IClassFixture<FastIdempotencyLeaseFactory>
{
    [Fact]
    public async Task LiveCompetingLease_WaiterGivesUpBoundedly_WithoutReinvokingTheHandler()
    {
        var email = $"wait-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(Cluckwork.Domain.Expenses.ExpenseCategory.Create(
                categoryId, accountId, Cluckwork.Domain.Accounts.SeedDefaults.FarmId, "Test-Category"));
            await db.SaveChangesAsync();
        });
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var key = Guid.NewGuid().ToString();
        var body = new
        {
            expenseCategoryId = categoryId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            description = "Live-lease wait probe",
            amountMinorUnits = 5_00L,
            flockId = (Guid?)null,
            note = (string?)null
        };

        var first = await client.PostWithKeyAsync("/api/v1/expenses", key, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Doctor the now-completed row back to InProgress with a lease far in
        // the future — simulating a competitor that is genuinely still alive
        // and working (never expires within this test).
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var record = await db.IdempotencyRecords.SingleAsync(r => r.AccountId == accountId);
            record.Status = IdempotencyStatus.InProgress;
            record.LeaseOwner = Guid.NewGuid();
            record.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            record.StatusCode = null;
            record.ContentType = null;
            record.ResponseBody = null;
            record.CompletedAt = null;
            await db.SaveChangesAsync();
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var waiter = await client.PostWithKeyAsync("/api/v1/expenses", key, body);
        sw.Stop();

        Assert.Equal(HttpStatusCode.Conflict, waiter.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"waiter took {sw.Elapsed}, expected a bounded give-up around the {FastIdempotencyLeaseFactory.MaxWaitSeconds}s MaxWaitSeconds setting");

        // The handler never ran again while the competing lease looked live —
        // still exactly the one row the FIRST (real) execution created.
        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(1, count);
    }
}
