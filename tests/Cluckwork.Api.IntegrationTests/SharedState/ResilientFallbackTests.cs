using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cluckwork.Api.IntegrationTests.SharedState;

// #543 — the resilient decorators' fallback POLICY, unit-tested against
// hand-written stubs (no Redis, no Docker, no fake clock):
//
//   - claim-once (grant replay)  FAILS CLOSED: Redis throws -> deny (false),
//     no in-process fallback — a per-process fallback would make the grant
//     usable once per replica.
//   - fixed-window counter       FALLS BACK to the in-process impl + alarm.
//
// "Alarm" = one LogWarning carrying the stable
// SecurityEvents.SharedStateRedisUnavailable name.
public sealed class ResilientFallbackTests
{
    // ── Recording logger ──────────────────────────────────────────────────

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
            public void Dispose()
            {
            }
        }
    }

    // ── Throwing stubs (Redis down) ───────────────────────────────────────

    private sealed class ThrowingClaimOnceStore : IClaimOnceStore
    {
        public bool TryClaim(string key, TimeSpan ttl) => throw new RedisException("down");
    }

    private sealed class ThrowingFixedWindowCounter : IFixedWindowCounter
    {
        public long Increment(string key, TimeSpan window) => throw new RedisException("down");

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            throw new RedisException("down");

        public ValueTask<FixedWindowResult> CountAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            throw new RedisException("down");
    }

    // ── Timing-out stubs (RedisTimeoutException — NOT a RedisException) ────

    private sealed class TimingOutClaimOnceStore : IClaimOnceStore
    {
        public bool TryClaim(string key, TimeSpan ttl) =>
            throw new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Unknown);
    }

    private sealed class TimingOutFixedWindowCounter : IFixedWindowCounter
    {
        public long Increment(string key, TimeSpan window) =>
            throw new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Unknown);

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            throw new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Unknown);

        public ValueTask<FixedWindowResult> CountAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            throw new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Unknown);
    }

    // ── Working stubs (Redis up) ──────────────────────────────────────────

    private sealed class WorkingClaimOnceStore : IClaimOnceStore
    {
        public bool TryClaim(string key, TimeSpan ttl) => true;
    }

    private sealed class WorkingFixedWindowCounter : IFixedWindowCounter
    {
        public long Increment(string key, TimeSpan window) => 42;

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            new(new FixedWindowResult(42, TimeSpan.FromMinutes(5)));

        public ValueTask<FixedWindowResult> CountAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            new(new FixedWindowResult(41, TimeSpan.FromMinutes(5)));
    }

    // ── 1. claim-once FAILS CLOSED ────────────────────────────────────────

    [Fact]
    public void ClaimOnce_WhenRedisThrows_DeniesAndAlarms()
    {
        var logger = new RecordingLogger<ResilientClaimOnceStore>();
        var store = new ResilientClaimOnceStore(new ThrowingClaimOnceStore(), logger);

        Assert.False(store.TryClaim("k", TimeSpan.FromMinutes(5)));

        Assert.Single(logger.Warnings);
        Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, logger.Warnings[0]);
    }

    [Fact]
    public void ClaimOnce_WhenRedisWorks_ReturnsTrueAndDoesNotAlarm()
    {
        var logger = new RecordingLogger<ResilientClaimOnceStore>();
        var store = new ResilientClaimOnceStore(new WorkingClaimOnceStore(), logger);

        Assert.True(store.TryClaim("k", TimeSpan.FromMinutes(5)));

        Assert.Empty(logger.Warnings);
    }

    // ── 3. counter FALLS BACK ─────────────────────────────────────────────

    [Fact]
    public void Counter_WhenRedisThrows_ReturnsFallbackValueAndAlarms()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var fallback = new InProcessFixedWindowCounter(new FakeTimeProvider());
        var counter = new ResilientFixedWindowCounter(new ThrowingFixedWindowCounter(), fallback, logger);

        Assert.Equal(1, counter.Increment("k", TimeSpan.FromMinutes(5)));

        Assert.Single(logger.Warnings);
        Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, logger.Warnings[0]);
    }

    [Fact]
    public void Counter_WhenRedisWorks_ReturnsRedisValueAndDoesNotAlarm()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var fallback = new InProcessFixedWindowCounter(new FakeTimeProvider());
        var counter = new ResilientFixedWindowCounter(new WorkingFixedWindowCounter(), fallback, logger);

        Assert.Equal(42, counter.Increment("k", TimeSpan.FromMinutes(5)));

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task CounterAsync_WhenRedisThrows_ReturnsFallbackValueAndAlarms()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var fallback = new InProcessFixedWindowCounter(new FakeTimeProvider());
        var counter = new ResilientFixedWindowCounter(new ThrowingFixedWindowCounter(), fallback, logger);

        var result = await counter.IncrementAsync("k", TimeSpan.FromMinutes(5));

        Assert.Equal(1, result.Count);
        Assert.True(result.Remaining > TimeSpan.Zero);
        Assert.Single(logger.Warnings);
        Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, logger.Warnings[0]);
    }

    [Fact]
    public async Task CounterAsync_WhenRedisWorks_ReturnsRedisValueAndDoesNotAlarm()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var fallback = new InProcessFixedWindowCounter(new FakeTimeProvider());
        var counter = new ResilientFixedWindowCounter(new WorkingFixedWindowCounter(), fallback, logger);

        var result = await counter.IncrementAsync("k", TimeSpan.FromMinutes(5));

        Assert.Equal(42, result.Count);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task CountAsync_WhenRedisThrows_ReadsTheFallbackAndAlarms()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var fallback = new InProcessFixedWindowCounter(new FakeTimeProvider());
        var counter = new ResilientFixedWindowCounter(new ThrowingFixedWindowCounter(), fallback, logger);
        await fallback.IncrementAsync("k", TimeSpan.FromMinutes(5));

        var result = await counter.CountAsync("k", TimeSpan.FromMinutes(5));

        Assert.Equal(1, result.Count);
        Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, Assert.Single(logger.Warnings));
    }

    [Fact]
    public async Task CountAsync_WhenRedisWorks_ReturnsRedisValueAndDoesNotAlarm()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var counter = new ResilientFixedWindowCounter(
            new WorkingFixedWindowCounter(), new InProcessFixedWindowCounter(new FakeTimeProvider()), logger);

        Assert.Equal(41, (await counter.CountAsync("k", TimeSpan.FromMinutes(5))).Count);
        Assert.Empty(logger.Warnings);
    }

    // Redis answers the read but refuses the increments, which land in the fallback and alarm.
    // The read must see them, or a budget kept in this counter never runs out.
    [Fact]
    public async Task CountAsync_WhenRedisReadsButRefusesIncrements_AddsTheFallbackCount()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var counter = new ResilientFixedWindowCounter(
            new ReadOnlyFixedWindowCounter(), new InProcessFixedWindowCounter(new FakeTimeProvider()), logger);

        await counter.IncrementAsync("k", TimeSpan.FromMinutes(5));
        await counter.IncrementAsync("k", TimeSpan.FromMinutes(5));
        var read = await counter.CountAsync("k", TimeSpan.FromMinutes(5));

        Assert.Equal(2, read.Count);
        Assert.Equal(2, logger.Warnings.Count);
        Assert.All(logger.Warnings, w => Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, w));
    }

    // Redis applied each increment, then the reply timed out, so the fallback counted it too.
    // Once reads recover, the same three increments must read as three, not six.
    [Fact]
    public async Task CountAsync_AfterIncrementsAppliedThenTimedOut_DoesNotDoubleCount()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var counter = new ResilientFixedWindowCounter(
            new AppliesThenTimesOutCounter(), new InProcessFixedWindowCounter(new FakeTimeProvider()), logger);

        for (var i = 0; i < 3; i++)
            await counter.IncrementAsync("k", TimeSpan.FromMinutes(5));
        var read = await counter.CountAsync("k", TimeSpan.FromMinutes(5));

        Assert.Equal(3, read.Count);
        Assert.Equal(3, logger.Warnings.Count);
    }

    private sealed class AppliesThenTimesOutCounter : IFixedWindowCounter
    {
        private readonly InProcessFixedWindowCounter applied = new(new FakeTimeProvider());

        public long Increment(string key, TimeSpan window)
        {
            applied.Increment(key, window);
            throw new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Unknown);
        }

        public async ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default)
        {
            await applied.IncrementAsync(key, window, cancellationToken);
            throw new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Unknown);
        }

        public ValueTask<FixedWindowResult> CountAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            applied.CountAsync(key, window, cancellationToken);
    }

    private sealed class ReadOnlyFixedWindowCounter : IFixedWindowCounter
    {
        public const long SharedCount = 1;

        public long Increment(string key, TimeSpan window) => throw new RedisException("NOPERM");

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            throw new RedisException("NOPERM");

        public ValueTask<FixedWindowResult> CountAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default) =>
            new(new FixedWindowResult(SharedCount, TimeSpan.FromMinutes(5)));
    }

    // ── Timeouts take the SAME path as other Redis failures ───────────────
    // RedisTimeoutException derives from TimeoutException, not RedisException —
    // these die if the catch clause is narrowed back to `catch (RedisException)`.

    [Fact]
    public void ClaimOnce_WhenRedisTimesOut_DeniesAndAlarms()
    {
        var logger = new RecordingLogger<ResilientClaimOnceStore>();
        var store = new ResilientClaimOnceStore(new TimingOutClaimOnceStore(), logger);

        Assert.False(store.TryClaim("k", TimeSpan.FromMinutes(5)));

        Assert.Single(logger.Warnings);
        Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, logger.Warnings[0]);
    }

    [Fact]
    public void Counter_WhenRedisTimesOut_ReturnsFallbackValueAndAlarms()
    {
        var logger = new RecordingLogger<ResilientFixedWindowCounter>();
        var fallback = new InProcessFixedWindowCounter(new FakeTimeProvider());
        var counter = new ResilientFixedWindowCounter(new TimingOutFixedWindowCounter(), fallback, logger);

        Assert.Equal(1, counter.Increment("k", TimeSpan.FromMinutes(5)));

        Assert.Single(logger.Warnings);
        Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, logger.Warnings[0]);
    }

}
