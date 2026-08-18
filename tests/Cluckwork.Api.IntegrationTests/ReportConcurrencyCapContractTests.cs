namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.SharedState;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.RateLimiting;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

// #545 — the per-account report concurrency cap on the shared lease backends.
// Deterministic (FakeTimeProvider drives the LEASE expiry clock; renewal is
// driven directly via RenewOnce because the repo's FakeTimeProvider cannot fire
// CreateTimer/Task.Delay). A shared InProcessLease stands in for "the store"
// two instances share; the Redis-backed proof of the same contract is in
// RedisReportConcurrencyCapContractTests.
public sealed class ReportConcurrencyCapContractTests
{
    // renewInterval is deliberately huge so the permit's background renewal loop
    // (a real-wall-clock Task.Delay) never fires inside a fast test; every test
    // that cares about renewal calls RenewOnce() directly.
    private static DistributedReportConcurrencyLimiter NewLimiter(
        ILease store, TimeProvider clock, int permitLimit,
        ILogger<DistributedReportConcurrencyLimiter>? logger = null,
        TimeSpan? ttl = null) =>
        new(store, new InProcessLease(clock), clock,
            logger ?? NullLogger<DistributedReportConcurrencyLimiter>.Instance,
            permitLimit, ttl ?? TimeSpan.FromSeconds(60), TimeSpan.FromHours(1));

    // Test 1 — two instances sharing one store enforce ONE combined count.
    [Fact]
    public async Task Two_instances_sharing_one_store_enforce_one_combined_count()
    {
        var clock = new FakeTimeProvider();
        var store = new InProcessLease(clock);
        var a = NewLimiter(store, clock, permitLimit: 2);
        var b = NewLimiter(store, clock, permitLimit: 2);
        var account = Guid.NewGuid();

        await using var p1 = await a.AcquireAsync(account);
        await using var p2 = await a.AcquireAsync(account);
        await using var p3 = await b.AcquireAsync(account);

        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Null(p3); // combined count is 2 across both instances, not 4
    }

    // Test 2 — a holder that dies without releasing has its slot reclaimed on TTL.
    [Fact]
    public async Task Dead_holder_slot_is_reclaimed_on_ttl_expiry()
    {
        var clock = new FakeTimeProvider();
        var store = new InProcessLease(clock);
        var limiter = NewLimiter(store, clock, permitLimit: 1, ttl: TimeSpan.FromSeconds(60));
        var account = Guid.NewGuid();

        var permit = await limiter.AcquireAsync(account);
        Assert.NotNull(permit);

        // Crash: never renew and never dispose — a crash never releases; the slot
        // leaks until TTL reclaims it. (The background renewal loop is parked on the
        // fake clock, so it never auto-renews; renewal in tests is driven via RenewOnce.)
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(await limiter.AcquireAsync(account)); // still wedged before TTL

        clock.Advance(TimeSpan.FromSeconds(2));
        await using var reacquired = await limiter.AcquireAsync(account);
        Assert.NotNull(reacquired); // reclaimed after TTL — account not permanently wedged

        // A's late release no-ops (slot re-granted) — harmless compare-and-delete.
        await permit!.DisposeAsync();
    }

    // Test 3 — a renewed permit is not evicted past its ORIGINAL ttl.
    [Fact]
    public async Task A_renewed_permit_is_not_evicted_past_its_original_ttl()
    {
        var clock = new FakeTimeProvider();
        var store = new InProcessLease(clock);
        var limiter = NewLimiter(store, clock, permitLimit: 1, ttl: TimeSpan.FromSeconds(60));
        var account = Guid.NewGuid();

        await using var permit = await limiter.AcquireAsync(account);
        Assert.NotNull(permit);

        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(RenewOutcome.Renewed, permit!.RenewOnce()); // extends to now+60 = t110

        clock.Advance(TimeSpan.FromSeconds(20)); // t70 — past the ORIGINAL 60s
        Assert.Null(await limiter.AcquireAsync(account)); // still held via renewal
    }

    // Test 4 — store unreachable: a local per-instance ceiling still applies and
    // alarms; NEVER unlimited (fail-closed capacity protection).
    [Fact]
    public async Task When_store_throws_a_local_ceiling_still_applies_and_alarms()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger<DistributedReportConcurrencyLimiter>();
        var limiter = new DistributedReportConcurrencyLimiter(
            new ThrowingLease(), new InProcessLease(clock), clock, logger,
            permitLimit: 2, ttl: TimeSpan.FromSeconds(60), renewInterval: TimeSpan.FromHours(1));
        var account = Guid.NewGuid();

        await using var p1 = await limiter.AcquireAsync(account);
        await using var p2 = await limiter.AcquireAsync(account);
        await using var p3 = await limiter.AcquireAsync(account);

        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Null(p3); // bounded to 2 per instance — never unlimited
        Assert.Contains(logger.Warnings, w => w.Contains(SecurityEvents.SharedStateRedisUnavailable));
    }

    // Test 5 — release is compare-and-delete: a previous holder must not free a
    // slot that expired and was re-granted to someone else.
    [Fact]
    public async Task Release_is_compare_and_delete_previous_holder_does_not_free_a_regranted_slot()
    {
        var clock = new FakeTimeProvider();
        var store = new InProcessLease(clock);
        var limiter = NewLimiter(store, clock, permitLimit: 1, ttl: TimeSpan.FromSeconds(60));
        var account = Guid.NewGuid();

        var a = await limiter.AcquireAsync(account);
        Assert.NotNull(a);
        // A stalls: it never renews (RenewOnce is never called). Its lease expires.

        clock.Advance(TimeSpan.FromSeconds(61)); // A's lease expires
        await using var b = await limiter.AcquireAsync(account);
        Assert.NotNull(b); // B re-acquires the same slot

        await a!.DisposeAsync(); // A releases — must NOT free B's slot
        Assert.Null(await limiter.AcquireAsync(account)); // B still holds it
    }

    // Test 6 — a permit acquired during a store outage is NOT silently evicted
    // when the store recovers (the review's TOP finding). PINNED renewal targets
    // the backend that granted the permit; a per-call decorator would re-probe
    // the recovered Redis, get false, and let the slot expire mid-report.
    [Fact]
    public async Task A_permit_from_an_outage_is_not_silently_evicted_when_the_store_recovers()
    {
        var clock = new FakeTimeProvider();
        var fallback = new InProcessLease(clock);
        var redis = new ToggleableLease(new InProcessLease(clock)) { Down = true };
        var ttl = TimeSpan.FromSeconds(60);
        var limiter = new DistributedReportConcurrencyLimiter(
            redis, fallback, clock, NullLogger<DistributedReportConcurrencyLimiter>.Instance,
            permitLimit: 1, ttl: ttl, renewInterval: TimeSpan.FromHours(1));
        var account = Guid.NewGuid();

        // Redis DOWN at acquire -> the permit pins to the in-process fallback.
        await using var permit = await limiter.AcquireAsync(account);
        Assert.NotNull(permit);

        redis.Down = false; // Redis RECOVERS

        clock.Advance(TimeSpan.FromSeconds(50));
        // Pinned to the fallback: renewal must still succeed against the backend
        // that granted the permit, not silently fail against the recovered Redis.
        Assert.Equal(RenewOutcome.Renewed, permit!.RenewOnce());

        // The fallback slot is still held past the ORIGINAL 60s TTL. The slot key
        // matches the limiter's format exactly (a golden — keep in sync).
        clock.Advance(TimeSpan.FromSeconds(20)); // t70
        Assert.False(fallback.TryAcquire($"report-cc:{account:N}:0", "someone-else", ttl));
    }

    // Test 7 — the scan continues past a taken slot; a second acquire lands in
    // the next slot rather than a false 429 (the review's same-slot-race concern,
    // as its deterministic algorithmic form).
    [Fact]
    public async Task Scan_continues_past_a_taken_slot_no_false_rejection()
    {
        var clock = new FakeTimeProvider();
        var store = new InProcessLease(clock);
        var limiter = NewLimiter(store, clock, permitLimit: 2);
        var account = Guid.NewGuid();

        await using var first = await limiter.AcquireAsync(account);  // slot 0
        await using var second = await limiter.AcquireAsync(account); // must find slot 1
        await using var third = await limiter.AcquireAsync(account);  // full -> refused

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);
    }

    // Test 8 — a NON-Redis fault on the pinned backend (e.g. an ObjectDisposedException
    // from a multiplexer torn down during shutdown) is a best-effort miss: RenewOnce
    // returns false and alarms, and DisposeAsync still releases without throwing. RED
    // if either catch is narrowed back to RedisException-only.
    [Fact]
    public async Task A_non_redis_fault_on_the_pinned_backend_alarms_and_still_releases()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger<DistributedReportConcurrencyLimiter>();
        var limiter = new DistributedReportConcurrencyLimiter(
            new DisposedLease(), new InProcessLease(clock), clock, logger,
            permitLimit: 1, ttl: TimeSpan.FromSeconds(60), renewInterval: TimeSpan.FromHours(1));
        var account = Guid.NewGuid();

        var permit = await limiter.AcquireAsync(account);
        Assert.NotNull(permit);

        Assert.Equal(RenewOutcome.Faulted, permit!.RenewOnce()); // non-Redis fault -> Faulted, not a throw
        await permit.DisposeAsync();               // must not throw though Release faults

        Assert.Contains(logger.Warnings, w => w.Contains(SecurityEvents.SharedStateRedisUnavailable));
    }

    private sealed class DisposedLease : ILease
    {
        public bool TryAcquire(string key, string owner, TimeSpan ttl) => true;
        public bool Renew(string key, string owner, TimeSpan ttl) => throw new ObjectDisposedException("mux");
        public bool Release(string key, string owner) => throw new ObjectDisposedException("mux");
    }

    // Test 9 — a report whose slot lapsed re-grabs a free slot on the next tick and
    // stays counted (owner decision: keep the running report accounted, never cancel).
    // RED if RenewTick does not re-acquire: the lapsed slot would be free and the
    // competitor below would acquire it.
    [Fact]
    public async Task A_lapsed_report_reacquires_a_free_slot_and_stays_counted()
    {
        var clock = new FakeTimeProvider();
        var store = new InProcessLease(clock);
        var limiter = NewLimiter(store, clock, permitLimit: 1, ttl: TimeSpan.FromSeconds(60));
        var account = Guid.NewGuid();

        await using var permit = await limiter.AcquireAsync(account);
        Assert.NotNull(permit);

        clock.Advance(TimeSpan.FromSeconds(61));            // the slot lapses (no renewal)
        Assert.Equal(RenewOutcome.Lost, permit!.RenewTick()); // Lost -> re-grabs the now-free slot

        Assert.Null(await limiter.AcquireAsync(account));  // re-counted: competitor refused
    }

    // Test 10 — a lapsed report with NO free slot to re-grab (another holder took the
    // only slot) alarms over-capacity and keeps running; it never frees the new holder.
    [Fact]
    public async Task A_lapsed_report_with_no_free_slot_alarms_over_capacity()
    {
        var clock = new FakeTimeProvider();
        var store = new InProcessLease(clock);
        var logger = new RecordingLogger<DistributedReportConcurrencyLimiter>();
        var limiter = NewLimiter(store, clock, permitLimit: 1, logger: logger, ttl: TimeSpan.FromSeconds(60));
        var account = Guid.NewGuid();

        var first = await limiter.AcquireAsync(account);
        Assert.NotNull(first);

        clock.Advance(TimeSpan.FromSeconds(61));            // first's slot lapses
        await using var second = await limiter.AcquireAsync(account); // second takes the only slot
        Assert.NotNull(second);

        Assert.Equal(RenewOutcome.Lost, first!.RenewTick()); // Lost, no free slot -> alarm
        Assert.Contains(logger.Warnings, w => w.Contains(SecurityEvents.ReportConcurrencyOverCapacity));

        Assert.Null(await limiter.AcquireAsync(account));  // did NOT steal second's slot
        await first.DisposeAsync();
    }

    private sealed class ThrowingLease : ILease
    {
        public bool TryAcquire(string key, string owner, TimeSpan ttl) => throw new RedisException("down");
        public bool Renew(string key, string owner, TimeSpan ttl) => throw new RedisException("down");
        public bool Release(string key, string owner) => throw new RedisException("down");
    }

    private sealed class ToggleableLease(ILease inner) : ILease
    {
        public bool Down { get; set; }
        public bool TryAcquire(string key, string owner, TimeSpan ttl) =>
            Down ? throw new RedisException("down") : inner.TryAcquire(key, owner, ttl);
        public bool Renew(string key, string owner, TimeSpan ttl) =>
            Down ? throw new RedisException("down") : inner.Renew(key, owner, ttl);
        public bool Release(string key, string owner) =>
            Down ? throw new RedisException("down") : inner.Release(key, owner);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
