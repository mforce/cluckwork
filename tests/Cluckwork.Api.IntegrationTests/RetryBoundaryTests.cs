namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// #269 review (codex findings on PR #350) — where the retry boundary is drawn.
//
// EnableRetryOnFailure makes EF replay a failed unit of work. That is only
// correct when the unit is REPLAYABLE. Two things in this codebase are not:
//
//   1. `next(context)` — the whole downstream HTTP pipeline. Re-running it
//      re-runs single-use, non-database side effects (the #308 step-up grant
//      is consumed in memory by CreateUserHandler/SetUserPasswordHandler) and
//      re-runs a domain state transition against state a prior attempt may
//      already have committed.
//   2. An "owned" AmbientTransaction unit — it can leave the failed attempt's
//      entities tracked as Added on the SAME AppDbContext (EF does not detach
//      them), and it can span a SESSION-scoped pg_advisory_lock that a
//      reconnect silently drops (FirstRunAdminService).
//
// So both regions run through the execution strategy — EF requires a
// user-initiated transaction to be opened inside one — but EXACTLY ONCE. What
// replaces a server-side replay for a write is the client retrying with the
// same Idempotency-Key, which #307's claim/lease/publish protocol already
// makes exactly-once. These tests pin that boundary from both sides.
public class RetryBoundaryFactory : CluckworkWebApplicationFactory
{
    public TransientCommandFaultInterceptor CommandFault { get; } = new();

    public TransientCommitFaultInterceptor CommitFault { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // A short bounded wait: one test below asserts the request does NOT
        // fall into the live-lease poll, and the pre-fix behaviour it guards
        // against polls that wait out in full before 409ing.
        builder.UseSetting("Idempotency:MaxWaitSeconds", "2");
        builder.UseSetting("Database:Resilience:MaxRetryDelaySeconds", "1");
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) =>
                options.AddInterceptors(CommandFault, CommitFault)));
    }
}

public sealed class RetryBoundaryTests : IClassFixture<RetryBoundaryFactory>, IDisposable
{
    private readonly RetryBoundaryFactory _factory;

    public RetryBoundaryTests(RetryBoundaryFactory factory) => _factory = factory;

    // Never leave a fault armed for the next [Fact] in the class.
    public void Dispose()
    {
        _factory.CommandFault.Disarm();
        _factory.CommitFault.Disarm();
    }

    private sealed record RecordedDto(Guid Id);
    private sealed record SubmitDto(Guid Id, string Status, List<Guid> EggLotIds);

    // --- codex 3696539077 (P2) ---
    // The lost commit acknowledgment. Postgres COMMITted the handler's
    // mutation AND the idempotency PUBLISH (one transaction, #307), and only
    // the acknowledgment was lost. Re-running `next(context)` from there makes
    // the handler observe its OWN already-committed transition and answer 422
    // — a 4xx telling the client its request was invalid, for a request that
    // in fact succeeded, and one no client will retry. The response the
    // request actually published must come back instead.
    [Fact]
    public async Task LostCommitAcknowledgment_OnAStateTransition_ReplaysThePublishedResponse_NotA422()
    {
        var email = $"retry-boundary-{Guid.NewGuid():N}@test.local";
        var accountId = await _factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await _factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await _factory.SeedFlockAsync(accountId, farmId);
        var client = _factory.CreateAuthedClient(await _factory.LoginForAccessTokenAsync(email));

        var recorded = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            new
            {
                farmId,
                houseId = Guid.NewGuid(),
                flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 1000,
                crackedEggs = 0,
                dirtyEggs = 0,
                discardedEggs = 0,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = 600 } }
            });
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var entryId = (await recorded.Content.ReadFromJsonAsync<RecordedDto>())!.Id;

        _factory.CommitFault.ArmOnce();
        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        // The commit really did land — proving this exercised the ambiguous
        // commit and not some earlier failure.
        Assert.True(_factory.CommitFault.Commits >= 1);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var submitted = await response.Content.ReadFromJsonAsync<SubmitDto>();
        Assert.Equal("Submitted", submitted!.Status);
        Assert.Single(submitted.EggLotIds);

        // Exactly once, not twice: the mutation must not have been replayed
        // on top of its own committed effect.
        var lots = await _factory.WithTenantScopeAsync(accountId,
            db => db.EggLots.CountAsync(l => l.FlockId == flockId));
        Assert.Equal(1, lots);
    }

    // --- codex 3696539079 (P2) ---
    // The claim INSERT is EF's own unit of work (no user-initiated
    // transaction), so the execution strategy replays it on its own. If the
    // first execution committed and only the acknowledgment was lost, the
    // replay hits the unique index — and inspection then has to recognise the
    // row as OURS. Classifying our own unexpired lease as a competing
    // LiveLease makes the request poll itself out and 409 without ever
    // invoking the handler.
    [Fact]
    public async Task LostAcknowledgmentOnTheClaimInsert_IsRecognisedAsOurOwnClaim_AndTheHandlerStillRuns()
    {
        var email = $"retry-boundary-{Guid.NewGuid():N}@test.local";
        var accountId = await _factory.SeedAccountWithUserAsync(email);
        var client = _factory.CreateAuthedClient(await _factory.LoginForAccessTokenAsync(email));

        var name = $"Claim Ack Loss {Guid.NewGuid():N}";
        _factory.CommandFault.Arm("INSERT INTO idempotency_records", afterExecution: true);
        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name, phone = "555-0100" });

        // Without the fix this is a 409 "still being processed" — the request
        // polling out the bounded wait against its OWN claim.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // The INSERT was genuinely executed twice — the first landed, the
        // second hit the unique index. Without that, this test proves nothing.
        Assert.Equal(2, _factory.CommandFault.Matches);

        var count = await _factory.WithTenantScopeAsync(accountId,
            db => db.Customers.CountAsync(c => c.Name == name));
        Assert.Equal(1, count);
    }

    // --- codex 3696539071 (P1) ---
    // An owned identity transaction that fails transiently leaves its
    // ApplicationUser tracked as Added — EF does not detach it, and generating
    // a fresh Guid for the retry does not either. Replaying the unit on the
    // SAME AppDbContext therefore flushes BOTH users, hitting the unique email
    // index; the operator sees a duplicate-key failure instead of the
    // connection failure that actually happened. The unit must run once.
    [Fact]
    public async Task TransientFailureCreatingAUser_IsNotReplayedOntoTheFailedAttemptsTrackedEntities()
    {
        var email = $"retry-boundary-{Guid.NewGuid():N}@test.local";
        var accountId = await _factory.SeedAccountWithUserAsync(email);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        var newEmail = $"created-{Guid.NewGuid():N}@test.local";
        _factory.CommandFault.Arm("INSERT INTO \"AspNetUsers\"", afterExecution: false);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => identity.CreateUserAsync(
            accountId, newEmail, FreshPassword(), role: null));

        var postgres = TransientFault.InnermostPostgres(thrown);
        Assert.NotNull(postgres);
        // The ORIGINAL transient failure, not a unique-violation manufactured
        // by a replay flushing the failed attempt's still-Added user.
        Assert.Equal(TransientFault.SqlState, postgres!.SqlState);
        Assert.Equal(1, _factory.CommandFault.Matches);

        var users = await _factory.WithTenantScopeAsync(accountId,
            db => db.Users.IgnoreQueryFilters().CountAsync(u => u.Email == newEmail));
        Assert.Equal(0, users);
    }

    // --- codex 3696894701 (P2) ---
    // ONE wrong password must cost exactly ONE failed access.
    //
    // AccessFailedAsync's save is a self-contained EF unit — login is anonymous
    // so IdempotencyMiddleware's tenant gate skips it, and /auth/step-up (the
    // other password oracle, #308) is on ResponseNotCacheable and skips it too.
    // Neither is wrapped in a user-initiated transaction, so under
    // EnableRetryOnFailure the execution strategy replays that save on its own.
    // When Postgres COMMITted the increment and only the ACKNOWLEDGMENT was
    // lost, the replay re-issues the UPDATE carrying the now-stale Identity
    // ConcurrencyStamp, matches 0 rows, and comes back as
    // IdentityResult.Failed — at that layer indistinguishable from losing a
    // race to a parallel writer. AccountLockout's reload loop then reloads the
    // ALREADY-incremented user and increments it a second time.
    //
    // The consequence is a real availability defect, not a cosmetic one: at two
    // increments per wrong password the #128 threshold of 5 is reached after 3
    // attempts. The identical "one wrong password, two failed accesses" bug
    // shipped once already on a different path (#336) and had to be fixed.
    //
    // PROVES: with a lost commit acknowledgment injected on that very UPDATE,
    // the failed-access save is executed EXACTLY ONCE and the counter moves by
    // EXACTLY ONE. Pre-fix both are wrong (3 executions, +2).
    // DOES NOT PROVE: anything about a genuine parallel conflict — the next
    // test covers that the reload loop survives this fix. Nor that a real
    // network-level ack loss is byte-for-byte this interceptor's throw; what it
    // pins is the classification the strategy actually keys on
    // (PostgresException.IsTransient, computed from SqlState).
    [Fact]
    public async Task LostAcknowledgmentOnTheFailedAccessUpdate_CountsOneWrongPasswordOnce_NotTwice()
    {
        var email = $"retry-boundary-{Guid.NewGuid():N}@test.local";
        var accountId = await _factory.SeedAccountWithUserAsync(email);

        _factory.CommandFault.Arm("UPDATE \"AspNetUsers\"", afterExecution: true);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password = FreshPassword() });
        _factory.CommandFault.Disarm();

        // The increment's UPDATE genuinely reached the server and committed —
        // the fault fires only from the post-execution hook. Without this the
        // test could pass having never exercised the ambiguous commit at all.
        Assert.True(_factory.CommandFault.Matches >= 1);

        // The load-bearing assertion, asserted FIRST because it is the one that
        // states the defect: one wrong password, one failed access. Pre-fix
        // this reads 2, so #128's threshold of 5 would fire after 3 attempts.
        Assert.Equal(1, await FailedAccessCountAsync(accountId, email));
        // And the mechanism behind it: the save was never replayed. Pre-fix
        // this reads 3 — the original, the strategy's replay, and the reload
        // loop's second AccessFailedAsync.
        Assert.Equal(1, _factory.CommandFault.Matches);

        // Deliberate, and exactly the pre-#269 behaviour: a transient failure
        // inside a single-attempt region surfaces instead of being absorbed,
        // so the caller does not get a 401 that silently under-counted. The
        // concrete 409 is Program.cs's existing global DbUpdateException
        // mapping ("Data conflict … Retry"), which this fix neither introduces
        // nor endorses — it is recorded so the surfacing is not mistaken for a
        // successful, absorbed retry. The client's remedy is to try again.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // --- codex 3696894701 (P2), the other side ---
    // The single-attempt boundary must not neuter the reload loop it wraps.
    // Two genuinely separate failed attempts that both read the user before
    // either writes are a real concurrency conflict: the loser MUST reload and
    // retry, or its increment is dropped and a distributed burst walks under
    // the #128 threshold — the exact attack AccountLockout exists to stop.
    //
    // Driven sequentially through two scoped UserManager/AppDbContext pairs so
    // the loser is deterministic rather than raced for.
    //
    // PROVES: after the fix, an IdentityResult.Failed caused by an ACTUAL
    // parallel writer is still reloaded and retried, so both attempts count.
    // DOES NOT PROVE: that concurrently-issued HTTP logins interleave this way
    // (AccountLockoutTests.Parallel_failed_attempts_are_all_counted_and_still_lock
    // covers that end-to-end); this pins the mechanism, not the scheduling.
    [Fact]
    public async Task AGenuineConcurrencyConflict_OnTheFailedAccessUpdate_IsStillReloadedAndRetried()
    {
        var email = $"retry-boundary-{Guid.NewGuid():N}@test.local";
        var accountId = await _factory.SeedAccountWithUserAsync(email);

        using var winner = _factory.Services.CreateScope();
        using var loser = _factory.Services.CreateScope();
        var winnerUsers = winner.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var loserUsers = loser.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Both scopes read BEFORE either writes, so the second one's tracked
        // ConcurrencyStamp is stale by the time it saves.
        var winnerUser = await winnerUsers.FindByEmailAsync(email);
        var loserUser = await loserUsers.FindByEmailAsync(email);
        Assert.NotNull(winnerUser);
        Assert.NotNull(loserUser);

        await AccountLockout.RecordFailedAccessAsync(
            winnerUsers, winner.ServiceProvider.GetRequiredService<AppDbContext>(), winnerUser);
        await AccountLockout.RecordFailedAccessAsync(
            loserUsers, loser.ServiceProvider.GetRequiredService<AppDbContext>(), loserUser);

        // Two distinct failed attempts, two increments: the loser reloaded and
        // retried rather than silently dropping its increment.
        Assert.Equal(2, await FailedAccessCountAsync(accountId, email));
    }

    private Task<int> FailedAccessCountAsync(Guid accountId, string email) =>
        _factory.WithTenantScopeAsync(accountId, db => db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Email == email)
            .Select(u => u.AccessFailedCount)
            .SingleAsync());

    // Generated per call — never a literal credential in source (AGENTS.md).
    internal static string FreshPassword() => $"Aa1!{Guid.NewGuid():N}";
}

// FirstRunAdminService provisions the single fixed SeedDefaults.AccountId, so
// this needs its own throwaway database rather than sharing one with the class
// above (mirrors FirstRunAdminUnlockCleanupTests' reasoning).
public sealed class BootstrapRetryBoundaryFactory : RetryBoundaryFactory;

public sealed class BootstrapRetryBoundaryTests : IClassFixture<BootstrapRetryBoundaryFactory>
{
    private readonly BootstrapRetryBoundaryFactory _factory;

    public BootstrapRetryBoundaryTests(BootstrapRetryBoundaryFactory factory)
    {
        _factory = factory;
        _ = _factory.Services; // force host + migration before the fault is armed
    }

    // --- codex 3696539074 (P2) ---
    // FirstRunAdminService holds a SESSION-scoped pg_advisory_lock across
    // "check for an Owner" AND "create the Owner". A reconnect releases that
    // lock; a retry that re-runs only the create delegate would therefore
    // re-execute the mutation with the lock gone and the Owner check never
    // repeated — two concurrent bootstrap-admin invocations could then both
    // mint an Owner. The concurrent race itself is not deterministically
    // reproducible, but its enabling condition is: the create region must be
    // executed exactly once per ProvisionAsync call.
    [Fact]
    public async Task TransientFailureInsideTheBootstrapCriticalSection_ExecutesTheCreateRegionExactlyOnce()
    {
        var email = $"bootstrap-retry-{Guid.NewGuid():N}@test.local";

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();

        _factory.CommandFault.Arm("INSERT INTO \"AspNetUsers\"", afterExecution: false);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.ProvisionAsync(email));
        _factory.CommandFault.Disarm();

        var postgres = TransientFault.InnermostPostgres(thrown);
        Assert.NotNull(postgres);
        Assert.Equal(TransientFault.SqlState, postgres!.SqlState);
        Assert.Equal(1, _factory.CommandFault.Matches);

        var owners = await _factory.WithTenantScopeAsync(SeedDefaults.AccountId,
            db => db.Users.IgnoreQueryFilters().CountAsync(u => u.AccountId == SeedDefaults.AccountId));
        Assert.Equal(0, owners);
    }
}
