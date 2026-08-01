namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Expenses;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// #307 PR review — two defects in the steal-loss branch of IdempotencyMiddleware
// (reached after OUR own PUBLISH loses its guard because our lease was stolen
// mid-execution):
//   1. RollbackAsync/CommitAsync do NOT return the connection to the ADO.NET
//      pool — only Dispose does. Waiting (WaitForCompletionAsync, up to
//      Idempotency:MaxWaitSeconds) BEFORE disposing pins a live Postgres
//      connection for the whole wait — a pool-exhaustion risk under
//      contention that did not exist before #307 (transactions used to be
//      scoped narrowly, not around the whole request plus a wait).
//   2. If the claim a loser is waiting on for someone ELSE to complete
//      vanishes (that someone else failed and released it), the loser polled
//      all the way to the deadline before giving up instead of noticing.
//
// Reproducing a genuine steal-loss deterministically (not a timing guess):
// this holds a REAL "FOR UPDATE" lock on the Account row — the same
// technique CurrencyLockRaceTests/TenantScopedLockTests use, confirmed via
// pg_blocking_pids — to park a real HTTP request's handler mid-flight
// (CreateExpenseHandler takes "FOR SHARE" on the same row), steals its OWN
// idempotency claim out from under it on a SEPARATE connection while it's
// parked, then releases the lock. The parked request's PUBLISH is then
// GUARANTEED to lose its guard (LeaseOwner no longer matches) — no timing
// luck for that part. The only non-deterministic-by-construction step is the
// brief "has the loser actually reached its WaitForCompletionAsync poll loop
// yet" handoff right after unblocking, for which there is no lock to
// synchronize on — a short fixed delay stands in there, bounded against a
// MaxWaitSeconds several times larger so it only affects WHEN the probes
// below run, never WHETHER the deterministic setup worked.
//
// The app's own connection pool is deliberately shrunk to ONE slot with a
// short acquire timeout, so a still-held connection during the wait is
// directly OBSERVABLE (a second, independent query starves/throws) rather
// than merely inferred. The lock-holder and the blocking-detection probe
// both use a DIFFERENT connection-string text than the app (Npgsql pools are
// keyed by the exact connection string), so neither competes for the app's
// single slot — only the app's own request and this test's later
// verification queries do.
public sealed class SmallPoolIdempotencyFactory : CluckworkWebApplicationFactory
{
    public const int MaxWaitSeconds = 8;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ConnectionStrings:Default", ConnectionString + ";Maximum Pool Size=1;Timeout=3");
        builder.UseSetting("Idempotency:MaxWaitSeconds", MaxWaitSeconds.ToString());
    }
}

public sealed class StealLossConnectionReleaseTests(SmallPoolIdempotencyFactory factory)
    : IClassFixture<SmallPoolIdempotencyFactory>
{
    private async Task<(Guid AccountId, Guid CategoryId, HttpClient Client)> SeedAsync()
    {
        var email = $"steal-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(ExpenseCategory.Create(
                categoryId, accountId, SeedDefaults.FarmId, "Steal-Category"));
            await db.SaveChangesAsync();
        });
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (accountId, categoryId, client);
    }

    // A lock-holder connection on the app's UNCONSTRAINED pool (a different
    // connection-string TEXT than the app's tiny one, so it never competes
    // for the app's single slot) holding a real "FOR UPDATE" on the Account
    // row — exactly what CreateExpenseHandler's "FOR SHARE" read blocks
    // behind.
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction, int Pid)> OpenLockHolderAsync(Guid accountId)
    {
        var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand(
            """SELECT 1 FROM "Accounts" WHERE "Id" = @id FOR UPDATE""", connection, transaction))
        {
            lockCmd.Parameters.AddWithValue("id", accountId);
            await lockCmd.ExecuteNonQueryAsync();
        }
        await using var pidCmd = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction);
        var pid = (int)(await pidCmd.ExecuteScalarAsync())!;
        return (connection, transaction, pid);
    }

    // Positive synchronization for "is the request parked behind holderPid's
    // lock", using its OWN separate connection (never the app's tiny pool —
    // that pool is exactly what this test is squeezing, so probing through
    // it would itself starve on the very condition being tested for).
    private async Task<bool> WaitUntilBlockedAsync(Task competing, int holderPid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (competing.IsCompleted) return false;

            await using var conn = new NpgsqlConnection(factory.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE pg_blocking_pids(pid) @> ARRAY[@holderPid]", conn);
            cmd.Parameters.AddWithValue("holderPid", holderPid);
            var blockedCount = (long)(await cmd.ExecuteScalarAsync())!;
            if (blockedCount > 0) return true;

            await Task.Delay(50);
        }
        throw new TimeoutException("Neither completion nor a lock wait was observed.");
    }

    [Fact]
    public async Task StealLoss_ReleasesTheConnection_AndGivesUpQuickly_WhenTheClaimVanishes()
    {
        var (accountId, categoryId, client) = await SeedAsync();
        var (holderConn, holderTx, holderPid) = await OpenLockHolderAsync(accountId);

        var key = Guid.NewGuid().ToString();
        var body = new
        {
            expenseCategoryId = categoryId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            description = "Steal-loss probe",
            amountMinorUnits = 5_00L,
            flockId = (Guid?)null,
            note = (string?)null
        };

        var sw = Stopwatch.StartNew();
        var requestTask = client.PostWithKeyAsync("/api/v1/expenses", key, body);

        var blocked = await WaitUntilBlockedAsync(requestTask, holderPid, TimeSpan.FromSeconds(15));
        Assert.True(blocked, "the request must park on the Account FOR UPDATE lock for this test to prove anything");

        // Steal the request's OWN claim out from under it, on the SAME held
        // connection/transaction as the lock — so releasing the lock and
        // publishing the steal happen atomically on commit.
        await using (var stealCmd = new NpgsqlCommand(
            """UPDATE idempotency_records SET "LeaseOwner" = @newOwner WHERE "AccountId" = @accountId""",
            holderConn, holderTx))
        {
            stealCmd.Parameters.AddWithValue("newOwner", Guid.NewGuid());
            stealCmd.Parameters.AddWithValue("accountId", accountId);
            var stolen = await stealCmd.ExecuteNonQueryAsync();
            Assert.Equal(1, stolen);
        }

        // Commit: releases the Account lock (unblocking the request) AND
        // publishes the steal in the SAME instant — the request's eventual
        // PUBLISH is now guaranteed to find LeaseOwner mismatched, not by
        // luck.
        await holderTx.CommitAsync();
        await holderConn.DisposeAsync();

        // No lock to synchronize on for "has the loser reached its
        // WaitForCompletionAsync poll loop yet" specifically — see the class
        // comment. MaxWaitSeconds (8s) is far longer than this, so it only
        // affects WHEN the probes below run.
        await Task.Delay(400);

        // Item 2: with the connection released before waiting, a SECOND,
        // INDEPENDENT connection attempt against the app's
        // Maximum-Pool-Size=1 pool must succeed quickly. If the loser is
        // still pinning the pool's only slot, this starves/throws
        // (Timeout=3 on the connection string) well before it would
        // otherwise succeed.
        var probeSw = Stopwatch.StartNew();
        var probeCount = await factory.WithTenantScopeAsync(accountId, db =>
            db.ExpenseCategories.CountAsync(c => c.Id == categoryId));
        probeSw.Stop();
        Assert.Equal(1, probeCount);
        Assert.True(probeSw.Elapsed < TimeSpan.FromSeconds(2),
            $"a second connection attempt took {probeSw.Elapsed} — the steal-loser is still pinning the pool's only " +
            "slot instead of releasing it before waiting");

        // Item 3: the claim the loser is waiting on for someone ELSE to
        // complete now vanishes (that someone else — from the loser's point
        // of view — failed and released it). The loser must give up
        // promptly, not poll out the rest of MaxWaitSeconds.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""DELETE FROM idempotency_records WHERE "AccountId" = {accountId}""");
        });

        var response = await requestTask;
        sw.Stop();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(SmallPoolIdempotencyFactory.MaxWaitSeconds - 2),
            $"the steal-loser took {sw.Elapsed} total — expected to give up promptly once its claim vanished, well " +
            $"under the {SmallPoolIdempotencyFactory.MaxWaitSeconds}s MaxWaitSeconds deadline");

        // Nobody's mutation is ever allowed to become durable once the claim
        // is gone: not the loser's own rolled-back attempt, and the loser
        // never re-runs the handler itself while merely watching for someone
        // else.
        var expenseCount = await factory.WithTenantScopeAsync(accountId, db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(0, expenseCount);
    }
}
