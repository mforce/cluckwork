namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.IntegrationTests.SharedState;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #308 / PR #336 review (3rd round) — the step-up registry's own contract, at
// the unit level. #338 (rework) moved both tables out of the process: replay
// now lives in IClaimOnceStore (#543) and logout revocation in the durable
// per-user integer ApplicationUser.StepUpLogoutEpoch, so the registry under
// test is the real PersistentStepUpGrantRegistry, constructed directly over an
// in-process claim store and the shared Postgres fixture (one seeded user).
//
// The finding these back up: the admission decision used to be TWO registry
// calls in StepUpGrantService — IsRevokedByLogout then TryConsume — over two
// independently-atomic ConcurrentDictionaries. A logout completing between
// them was invisible to the validation in flight, so a grant minted before a
// logout could still create an Owner or reset an Owner's password after it.
// The decision is now one operation, TryConsumeIfNotLoggedOutAsync, in which
// the claim is consumed FIRST and the logout epoch read SECOND — the ordering
// that replaced the single lock. StepUpAuthTests'
// ValidateAsync_MakesTheAdmissionDecisionInOneAtomicRegistryCall pins the
// call SHAPE; this file pins the SEMANTICS of the operation it calls.
//
// The revocation comparison is an INTEGER equality (the grant's embedded epoch
// vs the user's current epoch), never a wall clock — the #338 review defect —
// so the old tick-boundary tests are gone: there is no boundary to straddle.
//
// Multi-replica, fail-closed and the deterministic consume-before-read
// ordering test live in StepUpGrantRegistrySharedStoreTests (real Redis, two
// instances).
public sealed class StepUpGrantRegistryTests : IClassFixture<CluckworkWebApplicationFactory>
{
    private readonly CluckworkWebApplicationFactory factory;

    public StepUpGrantRegistryTests(CluckworkWebApplicationFactory factory) => this.factory = factory;

    // A fixed, whole-second UTC anchor for the claim windows. The revocation
    // logic never looks at it (it is an integer comparison), but the claim
    // TTLs must be positive and deterministic. Not DateTimeOffset.UtcNow —
    // nothing here should depend on the wall clock.
    private static readonly DateTimeOffset T =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    // Comfortably inside every grant's lifetime, so nothing below expires
    // by accident.
    private static DateTimeOffset Expiry => T.AddMinutes(5);

    // One user row per test run; every test below is single-threaded and
    // re-logs-out as needed, so a shared row is enough.
    private Guid _userId;

    // The seeded user's account: the TenantContext for every db this file opens.
    private Guid _accountId;

    // The fixture's tenant-scoped AppDbContext (scoped TenantContext resolved
    // to the user's account), over which the registry under test reads and
    // writes StepUpLogoutEpoch.
    private AppDbContext? _db;

    // A fresh db context over the same tenant — the concurrency test gives
    // every racer its own (DbContext is not thread-safe).
    private AppDbContext NewDb()
    {
        var tenant = new TenantContext();
        tenant.Resolve(_accountId);
        return new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant);
    }

    private IClaimOnceStore? _claimOnce;

    // #338 — IAsyncLifetime-style setup, but NOT declared: the class gets its
    // factory from the collection fixture, and re-declaring IAsyncLifetime
    // makes the xUnit analyzer demand a fixture source for the constructor
    // argument (the collection-fixture dispatch is not visible to it). The
    // shared store + db + user are prepared lazily, once, by InitializeAsync().
    private bool _initialized;

    private async Task InitializeAsync()
    {
        if (_initialized) return;
        var email = $"stepupreg-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _accountId = accountId;

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var user = await services.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync(email);
        Assert.NotNull(user);
        _userId = user.Id;

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant);

        // Fixed anchor so the claim TTLs are deterministic. The in-process
        // store owns TTL now; the registry only passes it through.
        _claimOnce = new InProcessClaimOnceStore(FixedFakeTimeProvider.At(T));
        _initialized = true;
    }

    // A fresh registry per case: same db (the state under test) and a fresh
    // claim store (replay state never crosses cases).
    private async Task<PersistentStepUpGrantRegistry> RegistryAsync()
    {
        await InitializeAsync();
        return new PersistentStepUpGrantRegistry(
            new InProcessClaimOnceStore(FixedFakeTimeProvider.At(T)), _db!);
    }

    // ---------- Logout revocation: epoch equality ----------

    // The refusal half of the contract, translated to epochs. A grant carrying
    // the epoch it was issued under is admitted while that epoch is current;
    // after RecordLogoutAsync (epoch N -> N+1) the same grant is refused. The
    // in-memory registry's "left unconsumed" property does not survive #338,
    // and this is the deliberate consequence: with no lock spanning Redis and
    // Postgres, the consume runs BEFORE the epoch read, so a revoked grant
    // burns its one-time claim slot on the way out (harmless — it is refused
    // either way, the claim self-expires, and the caller cannot tell the two
    // refusals apart). The registry's class comment names this accepted cost.
    //
    // Proven POSITIVELY that the refusal is BY EPOCH: the same jti, still
    // claimed in this case's shared store... no — this case's registry uses a
    // fresh claim store per call, so the same jti on a FRESH grant carrying
    // the post-logout epoch is admitted. That shows the jti is not burned in
    // any way that outlives the claim, and that a new grant under the new
    // epoch works.
    [Fact]
    public async Task ARevokedGrantIsRefused_ByEpochMismatch_AndTheSameJtiStillWorksOnAFreshGrant()
    {
        var registry = await RegistryAsync();
        var jti = Guid.NewGuid();

        // The user's epoch starts at 0 (HasDefaultValue); read it rather than
        // assuming, so the test holds if the default ever moves.
        var issueEpoch = await _db!.Users
            .Where(u => u.Id == _userId).Select(u => u.StepUpLogoutEpoch).FirstAsync();

        // Logout advances the epoch (N -> N+1).
        await registry.RecordLogoutAsync(_userId);

        // A grant still carrying the OLD epoch N is refused... (its one-time
        // claim slot is burned on the way out — the accepted cost named above.
        // A fresh claim store per registry means the jti is free again for the
        // next probe.)
        var registry2 = await RegistryAsync();
        Assert.False(await registry2.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, issueEpoch, Expiry, now: T));
        Assert.True(await registry2.IsRevokedByLogoutAsync(_userId, issueEpoch));

        // ...while a FRESH grant on the SAME jti, issued under the new epoch,
        // is admitted: the refusal was by epoch mismatch, not by the jti, and
        // replay-slot semantics are unchanged.
        var newEpoch = await _db.Users
            .Where(u => u.Id == _userId).Select(u => u.StepUpLogoutEpoch).FirstAsync();
        Assert.NotEqual(issueEpoch, newEpoch);
        var registry3 = await RegistryAsync();
        Assert.True(await registry3.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, newEpoch, Expiry, now: T));
        Assert.False(await registry3.IsRevokedByLogoutAsync(_userId, newEpoch));
    }

    // The over-revocation guard, at the unit level: recording a logout bounds
    // the PAST only. A grant minted afterwards is a fresh, deliberate
    // re-authentication and must work, or one logout would permanently lock the
    // user out of privileged administration.
    [Fact]
    public async Task AGrantIssuedAfterTheLogout_IsAdmitted()
    {
        var registry = await RegistryAsync();

        await registry.RecordLogoutAsync(_userId);

        var newEpoch = await _db!.Users
            .Where(u => u.Id == _userId).Select(u => u.StepUpLogoutEpoch).FirstAsync();
        Assert.True(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), newEpoch, Expiry, now: T));
    }

    // A logout is per-user. One user's logout must not revoke another's grant.
    [Fact]
    public async Task ALogoutRevokesOnlyThatUsersGrants()
    {
        var otherEmail = $"stepupreg-{Guid.NewGuid():N}@test.local";
        var otherAccountId = await factory.SeedAccountWithUserAsync(otherEmail);
        using var scope = factory.Services.CreateScope();
        var other = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync(otherEmail);
        Assert.NotNull(other);
        var otherUserId = other.Id;

        var registry = await RegistryAsync();
        await registry.RecordLogoutAsync(_userId);

        // The logged-out user's pre-logout grant (epoch 0) is refused.
        Assert.False(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), grantEpoch: 0, Expiry, now: T));
        // The other user's epoch was never touched, even though the
        // registry's db context is scoped to the first user's account: the
        // epoch read is by primary key and the row exists.
        Assert.True(await registry.TryConsumeIfNotLoggedOutAsync(
            otherUserId, Guid.NewGuid(), grantEpoch: 0, Expiry, now: T));
    }

    // ---------- The #492 fence: a stale full-entity write cannot revert the epoch ----------
    //
    // Codex #338 review, P1. RecordLogoutAsync is a bare ExecuteUpdate, NOT a
    // tracked SaveChanges, so it leaves ConcurrencyStamp untouched. Identity's
    // UserStore.UpdateAsync, however, issues a FULL-ENTITY update guarded on
    // that stamp. A concurrent same-user Identity write (login's
    // AccessFailedAsync / ResetAccessFailedCountAsync) that loaded the row
    // BEFORE the logout commits its stale epoch back over the unrotated stamp —
    // silently REVERTING the logout. RecordLogoutAsync therefore rotates the
    // stamp in the same statement, and this test pins that fence line: with
    // the stamp rotation deleted, the stale write below succeeds and reverts
    // the epoch to the pre-logout value, so the pre-logout grant is admitted
    // again and this test fails.
    //
    // The stale write must go through Identity's OWN store (the same
    // UserStore.UpdateAsync the hazard names): the repo's #492 Enable-path
    // comment records that a raw EF SaveChangesAsync on a tracked entity is
    // masked by EF's own ConcurrencyStamp token and goes red even with the
    // rotation deleted. UserStore issues a raw SQL UPDATE guarded on the
    // stamp that the rotation is meant to defeat.
    //
    // The UPDATE's SET list must be non-empty, or the test degenerates into
    // the same masked-guard the Enable comment names: an empty update reverts
    // nothing and goes green even with the fence deleted. The genuine change
    // is DisplayName. SecurityStamp is NO GOOD here: a stale snapshot's
    // SecurityStamp EQUALS the row's current one (the logout path rotates
    // only the ConcurrencyStamp, never the SecurityStamp), so SET
    // SecurityStamp = <stale> would write the CURRENT value back. Under
    // EnableRetryOnFailure an ambiguous-commit replay of that UPDATE would
    // then match 1 row on the re-issue (same stamp, current value), and the
    // test would go green on a fence that had already lost. DisplayName is
    // the change that is genuinely stale AND genuinely new.
    //
    // The stale context is a BARE one — deliberately NOT the app's options
    // (which carry EnableRetryOnFailure, #269). A transient blip mid-UPDATE
    // would replay the statement, and an ambiguous-commit replay of an
    // UPDATE whose WHERE matched 0 rows on the first attempt is exactly the
    // window that would re-match the row on the re-issue and mask the
    // mutation. This test is a deterministic CAS check, not a resilience
    // test; the retry semantics of RecordLogoutAsync itself are pinned by
    // the RETRY NOTE on the method and by the full suite.
    [Fact]
    public async Task RecordLogout_FencesAStaleFullEntityWrite_SoTheEpochCannotBeReverted()
    {
        var registry = await RegistryAsync();
        var jti = Guid.NewGuid();

        // 1. The starting epoch and stamp (the user's epoch starts at 0).
        var before = await _db!.Users
            .Where(u => u.Id == _userId)
            .Select(u => new { u.StepUpLogoutEpoch, u.ConcurrencyStamp }).FirstAsync();
        var preLogoutEpoch = before.StepUpLogoutEpoch;

        // 2. A SEPARATE tracked context loads the SAME row now — the concurrent
        //    Identity request that read the row before the logout. It holds the
        //    stale epoch and the stamp the fence is going to rotate underneath it.
        using var staleDb = NewDb();
        var stale = await staleDb.Users.SingleAsync(u => u.Id == _userId);
        Assert.Equal(preLogoutEpoch, stale.StepUpLogoutEpoch);

        // 3. The logout: epoch +1 AND the ConcurrencyStamp rotated away from
        //    what `stale` captured.
        await registry.RecordLogoutAsync(_userId);

        // 4. The stale full-entity write lands through Identity's own store —
        //    the exact UserStore.UpdateAsync the hazard names. With the fence
        //    in place its WHERE ConcurrencyStamp = <stale> matches 0 rows, so
        //    the store reports a concurrency failure and the epoch stays at
        //    preLogoutEpoch + 1. With the fence DELETED the stale snapshot
        //    writes its epoch (0) back over the unrotated stamp.
        // The store resolves through the scoped context it was constructed on
        // (the UserStore's AppDbContext) — so the user must be tracked on THAT
        // context, not on `staleDb`. Re-attach the stale snapshot there: the
        // raw UPDATE UserStore issues is still guarded on the stale stamp it
        // holds, which is the whole point of the test.
        using var userScope = factory.Services.CreateScope();
        var userManager = userScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        var storeDb = userScope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        storeDb.Users.Attach(stale);
        // A REAL stale write: one genuine change (DisplayName), and the stale
        // StepUpLogoutEpoch riding along in the full-entity SET list — which
        // is exactly what reverts the logout when the fence is deleted.
        //
        // The entity's ConcurrencyStamp is re-synced to the row's CURRENT
        // value BEFORE the UpdateAsync: UserStore's own UpdateAsync rotates
        // it again in-memory immediately before issuing the UPDATE
        // (UserManager.UpdateSecurityStampAsync — the same call the #492
        // Enable path uses, and the reason that path's FENCE rotates it a
        // SECOND time), which puts the NEW value in both SET and WHERE and
        // defeats the fence by accident. A genuine stale writer does not
        // know the new value; the stale snapshot is what it holds, and that
        // is what must appear in the UPDATE's SET and WHERE. The fence is
        // still under test: the logout rotated the row's stamp away from
        // what this snapshot captured, so a SET + WHERE on the stale value
        // matches 0 rows. (This is NOT the #492 masked-guard — that one is
        // EF's own ConcurrencyStamp token catching a raw SaveChangesAsync;
        // here the WHERE is the stale writer's own, and the fence is what
        // makes it lose.)
        var currentStamp = await storeDb.Users
            .Where(u => u.Id == _userId).Select(u => u.ConcurrencyStamp).FirstAsync();
        stale.ConcurrencyStamp = currentStamp;
        stale.DisplayName = "stale-identity-write";
        var updateResult = await userManager.UpdateAsync(stale);
        Assert.Equal("stale-identity-write", stale.DisplayName);

        // The fence: the UPDATE's WHERE matched the pre-logout stamp, which
        // the fence rotated away — so 0 rows matched, and UserStore reports
        // a concurrency failure. This is the EXPECTED outcome when the fence
        // is in place. (Without the fence the WHERE would have matched the
        // unrotated stamp, the stale epoch would have been written back, and
        // the epoch assertion below would have failed.)
        Assert.False(updateResult.Succeeded,
            "with the fence the stale write must LOSE its CAS (0 rows matched)");
        var after = await _db.Users
            .Where(u => u.Id == _userId).Select(u => u.StepUpLogoutEpoch).FirstAsync();
        Assert.True(after == preLogoutEpoch + 1,
            $"the logout's epoch bump must stand — the stale write may not revert it (after={after}, preLogoutEpoch={preLogoutEpoch})");

        // 5. End-to-end property: the logout STUCK. A grant captured pre-logout
        //    (carrying the pre-logout epoch) is still refused.
        var fresh = await RegistryAsync();
        Assert.False(await fresh.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, preLogoutEpoch, Expiry, now: T));
    }

    // ---------- Replay ----------

    [Fact]
    public async Task ASecondConsumeOfTheSameJti_IsRefused()
    {
        var registry = await RegistryAsync();
        var jti = Guid.NewGuid();

        Assert.True(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, grantEpoch: 0, Expiry, now: T));
        Assert.False(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, grantEpoch: 0, Expiry, now: T));
    }

    // Replay tracking is keyed by jti alone — a second, DIFFERENT grant for the
    // same user is a separate token and must be admitted. Otherwise using one
    // grant would lock the user out of taking another.
    [Fact]
    public async Task ADifferentJtiForTheSameUser_IsStillAdmitted()
    {
        var registry = await RegistryAsync();

        Assert.True(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), grantEpoch: 0, Expiry, now: T));
        Assert.True(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), grantEpoch: 0, Expiry, now: T));
    }

    // ---------- RecordLogout advances the epoch, never decreases it ----------
    //
    // One logout can reach the registry twice for the same user (the cookie
    // owner and the authenticated bearer are recorded independently —
    // IdentityProvider), and logouts arrive out of order under concurrency.
    // The epoch must only ever move forward, or a late-delivered EARLIER
    // record would silently un-revoke grants a later one had already killed.
    [Fact]
    public async Task RecordLogout_StrictlyIncreasesTheEpoch_AndNeverDecreasesIt()
    {
        var registry = await RegistryAsync();

        var before = await _db!.Users
            .Where(u => u.Id == _userId).Select(u => u.StepUpLogoutEpoch).FirstAsync();
        await registry.RecordLogoutAsync(_userId);
        await registry.RecordLogoutAsync(_userId); // the same logout's second axis
        var after = await _db.Users
            .Where(u => u.Id == _userId).Select(u => u.StepUpLogoutEpoch).FirstAsync();

        // Strictly increased by exactly one per call — and a grant issued
        // under `before` is now refused, i.e. it can never un-revoke.
        Assert.Equal(before + 2, after);
        Assert.False(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), grantEpoch: before, Expiry, now: T));
    }

    // ---------- Non-claim window ----------
    //
    // Pruning was an in-memory-table detail; the claim-once store owns TTL now.
    // What the registry itself still guards is the non-positive TTL: an
    // expired grant is refused WITHOUT touching the store, so it cannot burn
    // a claim slot (or, worse, throw from a store that rejects zero TTLs).
    [Fact]
    public async Task AnExpiredClaimWindow_IsRefusedWithoutConsuming()
    {
        var registry = await RegistryAsync();
        var jti = Guid.NewGuid();

        // ttl == 0 — not a valid claim window.
        Assert.False(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, grantEpoch: 0, expiresAt: T, now: T));
        // ttl < 0 — the grant lapsed before validation even started.
        Assert.False(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, grantEpoch: 0, expiresAt: T.AddSeconds(-1), now: T));

        // The jti was never claimed: a live window on the same jti is admitted.
        Assert.True(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, grantEpoch: 0, expiresAt: Expiry, now: T));
    }

    // ---------- SUPPLEMENTARY: concurrency ----------
    //
    // Deliberately labelled supplementary. A timing test can pass by luck, so
    // it guards NOTHING on its own — the deterministic guards are the
    // spy-registry test in StepUpAuthTests (the call shape) plus the semantic
    // cases above and the ordering test in the shared-store file. What this
    // adds is the one property only concurrency can show: the invariant holds
    // while RecordLogoutAsync is hammering the same database, which is the
    // traffic pattern the finding was about.
    //
    // The invariant asserted is single-use under contention: N threads racing
    // to consume ONE jti must produce EXACTLY one winner. The winner is decided
    // by the shared in-process claim store (its lock serialises the claim);
    // the db round trips are independent SELECTs. Concurrent logout traffic on
    // an UNRELATED user runs alongside, so StepUpLogoutEpoch is being written
    // from multiple threads throughout without touching this user's epoch.
    [Fact]
    public async Task Supplementary_UnderConcurrentLogoutTraffic_AJtiIsConsumedExactlyOnce()
    {
        const int rounds = 30;
        const int racers = 8;

        // A second user absorbs the concurrent logout writes.
        var otherEmail = $"stepupreg-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(otherEmail);
        using var otherScope = factory.Services.CreateScope();
        var other = await otherScope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync(otherEmail);
        Assert.NotNull(other);
        var otherUserId = other.Id;

        for (var round = 0; round < rounds; round++)
        {
            await InitializeAsync();
            var jti = Guid.NewGuid();
            var now = T.AddMinutes(round);

            using var start = new Barrier(racers * 2);
            var winners = 0;
            // DbContext is not thread-safe: each racer gets its own context
            // (and its own registry instance over the shared claim store).
            // That is exactly the multi-request topology — the in-process
            // claim store is what decides the single winner.
            var racerDbs = Enumerable.Range(0, racers).Select(_ => NewDb()).ToArray();
            var writerDbs = Enumerable.Range(0, racers).Select(_ => NewDb()).ToArray();
            var claimOnce = new InProcessClaimOnceStore(FixedFakeTimeProvider.At(now));

            var tasks = new List<Task>();
            for (var i = 0; i < racers; i++)
            {
                var consumeDb = racerDbs[i];
                tasks.Add(Task.Run(async () =>
                {
                    start.SignalAndWait();
                    if (await new PersistentStepUpGrantRegistry(claimOnce, consumeDb)
                            .TryConsumeIfNotLoggedOutAsync(
                                _userId, jti, grantEpoch: 0, now.AddMinutes(5), now))
                        Interlocked.Increment(ref winners);
                }));

                // Logouts for ANOTHER user: they must not affect this round's
                // outcome, but they keep the epoch column under concurrent
                // mutation while the consumes run. Each writer has its own
                // context — DbContext is not thread-safe.
                var writerDb = writerDbs[i];
                tasks.Add(Task.Run(async () =>
                {
                    start.SignalAndWait();
                    await new PersistentStepUpGrantRegistry(
                        new InProcessClaimOnceStore(FixedFakeTimeProvider.At(now)),
                        writerDb).RecordLogoutAsync(otherUserId);
                }));
            }

            await Task.WhenAll(tasks);
            var allDbs = new List<AppDbContext>(racerDbs);
            allDbs.AddRange(writerDbs);
            foreach (var db in allDbs)
                await db.DisposeAsync();

            Assert.Equal(1, winners);
        }
    }
}

// A FakeTimeProvider pre-advanced to a fixed anchor: the claim stores here only
// need a deterministic "now" for TTL bookkeeping; the revocation logic itself
// never reads the clock (it is an integer epoch comparison).
internal static class FixedFakeTimeProvider
{
    public static FakeTimeProvider At(DateTimeOffset anchor)
    {
        // FakeTimeProvider.UtcNow is get-only; Advance is the public setter.
        var clock = new FakeTimeProvider();
        clock.Advance(anchor - clock.UtcNow);
        return clock;
    }
}
