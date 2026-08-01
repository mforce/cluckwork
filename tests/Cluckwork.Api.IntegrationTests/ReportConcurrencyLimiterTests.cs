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
