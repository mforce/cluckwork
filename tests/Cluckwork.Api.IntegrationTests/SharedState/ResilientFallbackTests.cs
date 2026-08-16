namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #543 — the resilient decorators' fallback POLICY, unit-tested against
// hand-written stubs (no Redis, no Docker, no fake clock):
//
//   - claim-once (grant replay)  FAILS CLOSED: Redis throws -> deny (false),
//     no in-process fallback — a per-process fallback would make the grant
//     usable once per replica.
//   - fixed-window counter       FALLS BACK to the in-process impl + alarm.
//   - lease                      FALLS BACK to the in-process impl + alarm,
//     on every method.
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
    }

    private sealed class ThrowingLease : ILease
    {
        public bool TryAcquire(string key, string owner, TimeSpan ttl) => throw new RedisException("down");
        public bool Renew(string key, string owner, TimeSpan ttl) => throw new RedisException("down");
        public bool Release(string key, string owner) => throw new RedisException("down");
    }

    // ── Working stubs (Redis up) ──────────────────────────────────────────

    private sealed class WorkingClaimOnceStore : IClaimOnceStore
    {
        public bool TryClaim(string key, TimeSpan ttl) => true;
    }

    private sealed class WorkingFixedWindowCounter : IFixedWindowCounter
    {
        public long Increment(string key, TimeSpan window) => 42;
    }

    private sealed class WorkingLease : ILease
    {
        public bool TryAcquire(string key, string owner, TimeSpan ttl) => true;
        public bool Renew(string key, string owner, TimeSpan ttl) => true;
        public bool Release(string key, string owner) => true;
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

    // ── 4. lease FALLS BACK on every method ───────────────────────────────

    [Fact]
    public void Lease_WhenRedisThrows_DelegatesToFallbackAndAlarmsPerCall()
    {
        var logger = new RecordingLogger<ResilientLease>();
        var fallback = new InProcessLease(new FakeTimeProvider());
        var lease = new ResilientLease(new ThrowingLease(), fallback, logger);

        Assert.True(lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));
        Assert.True(lease.Renew("k", "owner-1", TimeSpan.FromMinutes(5)));
        Assert.True(lease.Release("k", "owner-1"));

        // One warning per fallen-back call.
        Assert.Equal(3, logger.Warnings.Count);
        foreach (var warning in logger.Warnings)
            Assert.Contains(SecurityEvents.SharedStateRedisUnavailable, warning);
    }

    [Fact]
    public void Lease_WhenRedisWorks_ReturnsRedisResultsAndDoesNotAlarm()
    {
        var logger = new RecordingLogger<ResilientLease>();
        var fallback = new InProcessLease(new FakeTimeProvider());
        var lease = new ResilientLease(new WorkingLease(), fallback, logger);

        Assert.True(lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));
        Assert.True(lease.Renew("k", "owner-1", TimeSpan.FromMinutes(5)));
        Assert.True(lease.Release("k", "owner-1"));

        Assert.Empty(logger.Warnings);
    }
}
