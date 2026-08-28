namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.IntegrationTests.SharedState;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// #338 — the point of the shared-store move, at the unit level: ONE Redis
// claim store and ONE Postgres shared by TWO independent registry instances
// (regA/regB), which is exactly the multi-replica topology the in-memory
// registry could never see. Revocation here is the per-user integer epoch
// (ApplicationUser.StepUpLogoutEpoch) compared for equality — the #338 rework
// of the clock-skew finding — so nothing in this file compares wall clocks.
public sealed class StepUpGrantRegistrySharedStoreTests :
    IClassFixture<CluckworkWebApplicationFactory>, IClassFixture<RedisFixture>
{
    private readonly CluckworkWebApplicationFactory factory;
    private readonly RedisFixture redis;

    public StepUpGrantRegistrySharedStoreTests(
        CluckworkWebApplicationFactory factory, RedisFixture redis)
    {
        this.factory = factory;
        this.redis = redis;
    }

    // A fixed anchor for the claim windows. The revocation logic never looks
    // at it (it is an integer comparison), but the claim TTLs must be
    // deterministic. Not DateTimeOffset.UtcNow.
    private static readonly DateTimeOffset T =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Expiry => T.AddMinutes(5);

    private Guid _userId;
    private Guid _accountId;
    private AppDbContext? _db;
    private IClaimOnceStore? _claimOnce;

    // Two registries over the SAME store and the SAME database: the replica
    // pair. The whole point is a SHARED claim namespace and a SHARED epoch
    // column, so both instances see every other's claims and logouts.
    private PersistentStepUpGrantRegistry? _regA;
    private PersistentStepUpGrantRegistry? _regB;

    // IAsyncLifetime-style setup, but NOT declared: the class gets its
    // fixtures from constructor arguments and re-declaring IAsyncLifetime
    // makes the xUnit analyzer demand a fixture source for them (the
    // collection-fixture dispatch is not visible to it). The shared store + db
    // + user are prepared lazily, once, by InitializeAsync().
    private bool _initialized;

    private async Task InitializeAsync()
    {
        if (_initialized) return;
        var email = $"stepupreg2-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _accountId = accountId;

        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync(email);
        Assert.NotNull(user);
        _userId = user.Id;

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());

        // One REAL Redis claim store, one namespace, two registries — the
        // replica pair. Real (short) TTLs: Redis honours its server clock, not
        // the fake one.
        _claimOnce = new RedisClaimOnceStore(redis.Redis, Guid.NewGuid().ToString("N"));
        _regA = new PersistentStepUpGrantRegistry(_claimOnce, _db);
        _regB = new PersistentStepUpGrantRegistry(_claimOnce, _db);
        _initialized = true;
    }

    // (a) Replay crosses instances: a jti consumed via regA is refused via regB.
    [Fact]
    public async Task AClaimConsumedOnOneInstance_IsRefusedOnTheOther()
    {
        await InitializeAsync();
        var jti = Guid.NewGuid();
        var epoch = await CurrentEpochAsync();

        Assert.True(await _regA!.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, epoch, Expiry, now: T));
        Assert.False(await _regB!.TryConsumeIfNotLoggedOutAsync(
            _userId, jti, epoch, Expiry, now: T));
    }

    // (b) Logout revocation crosses instances: a logout recorded on regA
    // (epoch bump in shared Postgres) refuses a grant carrying the pre-logout
    // epoch, presented to regB.
    [Fact]
    public async Task ALogoutRecordedOnOneInstance_RevokesOnTheOther()
    {
        await InitializeAsync();
        var preEpoch = await CurrentEpochAsync();
        await _regA!.RecordLogoutAsync(_userId);

        // A grant carrying the pre-logout epoch is refused via regB...
        Assert.False(await _regB!.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), preEpoch, Expiry, now: T));
        // ...while a grant issued under the post-logout epoch is still
        // admitted — the revocation bounds the past, not the user.
        var postEpoch = await CurrentEpochAsync();
        Assert.NotEqual(preEpoch, postEpoch);
        Assert.True(await _regB.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), postEpoch, Expiry, now: T));
    }

    // (c) FAIL-CLOSED: when the shared store is unreachable, the admission
    // decision is DENIED rather than admitted without a replay proof. The
    // ResilientClaimOnceStore is the production wiring (Redis throws → false);
    // the stub below reproduces its contract exactly the way
    // ResilientFallbackTests does, so this pins the registry's behaviour on
    // that input: a claim denial is an admission denial, and it is indistinct
    // from a replay (a bare false).
    [Fact]
    public async Task WhenTheClaimStoreIsUnreachable_TheGrantIsDenied_FailClosed()
    {
        await InitializeAsync();
        var registry = new PersistentStepUpGrantRegistry(
            new ResilientClaimOnceStore(
                new UnreachableClaimOnceStore(), new NullLogger<ResilientClaimOnceStore>()),
            _db!);

        Assert.False(await registry.TryConsumeIfNotLoggedOutAsync(
            _userId, Guid.NewGuid(), grantEpoch: 0, Expiry, now: T));
    }

    // (d) ORDERING — the deterministic guard the pre-rework suite lacked
    // (finding #2). The decorating claim store below calls RecordLogoutAsync
    // on the SAME database, as a side effect of TryClaim, BEFORE returning
    // true — i.e. it simulates "a logout lands in the gap between the consume
    // and the epoch read". The registry MUST refuse: only the shipped order
    // (consume FIRST, read SECOND) catches an increment in that gap. If the
    // epoch read were moved BEFORE the consume, the read would see the
    // pre-logout epoch, the consume would then fire the logout, and the grant
    // would be ADMITTED — so this test passes ONLY when the epoch read runs
    // AFTER the consume, and FAILS if the read is moved before the consume.
    [Fact]
    public async Task ALogoutLandingInTheGapBetweenConsumeAndRead_IsCaught()
    {
        await InitializeAsync();
        var preEpoch = await CurrentEpochAsync();
        var jti = Guid.NewGuid();

        // A fresh db for the side effect: it must not share a change tracker
        // with the registry under test.
        var tenant = new TenantContext();
        tenant.Resolve(_accountId);
        var sideEffectDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());

        var gapLogoutClaimOnce = new LogoutOnClaimClaimOnceStore(
            _claimOnce!, sideEffectDb, _userId);
        var registry = new PersistentStepUpGrantRegistry(gapLogoutClaimOnce, _db!);

        try
        {
            Assert.False(await registry.TryConsumeIfNotLoggedOutAsync(
                _userId, jti, preEpoch, Expiry, now: T));
        }
        finally
        {
            await sideEffectDb.DisposeAsync();
        }
    }

    private async Task<int> CurrentEpochAsync()
    {
        await InitializeAsync();
        return await _db!.Users
            .Where(u => u.Id == _userId).Select(u => u.StepUpLogoutEpoch).FirstAsync();
    }

    // (d)'s side-effecting store: forwards to the real store, and — on the
    // FIRST successful claim for the target user's grant — advances that
    // user's epoch in the side-effect database before returning true. That is
    // exactly "a logout commits between the consume and the epoch read".
    private sealed class LogoutOnClaimClaimOnceStore(
        IClaimOnceStore inner, AppDbContext db, Guid userId) : IClaimOnceStore
    {
        public bool TryClaim(string key, TimeSpan ttl)
        {
            if (!inner.TryClaim(key, ttl))
                return false;

            db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdate(
                    s => s.SetProperty(u => u.StepUpLogoutEpoch, u => u.StepUpLogoutEpoch + 1));
            return true;
        }
    }

    // The fail-closed input: a store that throws exactly like a dead Redis
    // connection does. Mirrors ResilientFallbackTests' ThrowingClaimOnceStore.
    private sealed class UnreachableClaimOnceStore : IClaimOnceStore
    {
        public bool TryClaim(string key, TimeSpan ttl) =>
            throw new StackExchange.Redis.RedisException("down");
    }
}
