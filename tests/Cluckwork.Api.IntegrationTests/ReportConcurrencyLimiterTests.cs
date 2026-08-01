namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.RateLimiting;

// #311 — pure logic tests for the per-account report concurrency cap: no
// HTTP, no database, so the acquire/reject/release behaviour is asserted
// directly and deterministically rather than inferred from real concurrent
// requests (that end-to-end wiring is covered separately by
// ReportsConcurrencyLimitTests).
public sealed class ReportConcurrencyLimiterTests
{
    private static RateLimitingOptions OptionsWithLimit(int permitLimit) => new()
    {
        ReportsConcurrency = new RateLimitingOptions.ConcurrencyPolicy
        {
            PermitLimit = permitLimit,
            QueueLimit = 0,
        }
    };

    [Fact]
    public void Acquire_up_to_the_limit_then_rejects_the_same_account()
    {
        using var limiter = new ReportConcurrencyLimiter(OptionsWithLimit(2));
        var accountId = Guid.NewGuid();

        using var first = limiter.Acquire(accountId);
        using var second = limiter.Acquire(accountId);
        using var third = limiter.Acquire(accountId);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(third.IsAcquired);
    }

    [Fact]
    public void A_saturated_account_does_not_affect_a_different_account()
    {
        using var limiter = new ReportConcurrencyLimiter(OptionsWithLimit(1));
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        using var heldForA = limiter.Acquire(accountA);
        using var rejectedForA = limiter.Acquire(accountA);
        using var okForB = limiter.Acquire(accountB);

        Assert.True(heldForA.IsAcquired);
        Assert.False(rejectedForA.IsAcquired);
        Assert.True(okForB.IsAcquired);
    }

    // #311 — pins the "refuse, never queue" contract the GLOSSARY and Help copy
    // promise, under the one configuration that could plausibly break it. Note
    // what this test does and does not catch: it does NOT distinguish
    // QueueLimit = 0 from QueueLimit = 5 in the partition options, because
    // AttemptAcquire never consults the queue either way — that indistinguishability
    // IS the bug, and it is why the boot guard in RateLimitingOptions.Validate,
    // not this test, is the real fix. What it does catch is the change that would
    // make the queue live: swapping Acquire's non-waiting AttemptAcquire for a
    // waiting acquire, which would silently start parking over-cap reports
    // instead of returning the documented 429.
    [Fact]
    public void An_over_cap_acquire_refuses_instead_of_waiting_even_with_a_queue_configured()
    {
        var options = new RateLimitingOptions
        {
            ReportsConcurrency = new RateLimitingOptions.ConcurrencyPolicy
            {
                PermitLimit = 1,
                QueueLimit = 5,
            }
        };
        using var limiter = new ReportConcurrencyLimiter(options);
        var accountId = Guid.NewGuid();

        using var held = limiter.Acquire(accountId);
        using var overCap = limiter.Acquire(accountId);

        Assert.True(held.IsAcquired);
        Assert.False(overCap.IsAcquired);
    }

    [Fact]
    public void Releasing_a_lease_frees_the_permit_for_the_same_account()
    {
        using var limiter = new ReportConcurrencyLimiter(OptionsWithLimit(1));
        var accountId = Guid.NewGuid();

        var first = limiter.Acquire(accountId);
        Assert.True(first.IsAcquired);
        first.Dispose();

        using var second = limiter.Acquire(accountId);
        Assert.True(second.IsAcquired);
    }
}
