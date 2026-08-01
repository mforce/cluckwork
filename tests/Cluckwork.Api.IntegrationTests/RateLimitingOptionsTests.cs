namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.RateLimiting;

// #143 — bad limiter config must fail at boot (Validate), not throw a 500 from
// inside the partition factory on the first login request.
public sealed class RateLimitingOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var ex = Record.Exception(() => new RateLimitingOptions().Validate());
        Assert.Null(ex);
    }

    [Fact]
    public void Zero_login_permit_limit_is_rejected()
    {
        var options = new RateLimitingOptions
        {
            Login = new RateLimitingOptions.FixedWindow { PermitLimit = 0, WindowSeconds = 900 }
        };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Negative_refresh_window_is_rejected()
    {
        var options = new RateLimitingOptions
        {
            Refresh = new RateLimitingOptions.FixedWindow { PermitLimit = 10, WindowSeconds = -1 }
        };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Malformed_trusted_proxy_cidr_is_rejected()
    {
        var options = new RateLimitingOptions { TrustedProxies = ["not-a-cidr"] };
        Assert.ThrowsAny<Exception>(options.Validate);
    }

    [Fact]
    public void Valid_trusted_proxy_cidr_parses()
    {
        var options = new RateLimitingOptions { TrustedProxies = ["172.16.0.0/12", "10.0.0.1/32"] };
        var networks = options.ParseTrustedProxies();
        Assert.Equal(2, networks.Length);
    }

    // #311
    [Fact]
    public void Zero_reports_concurrency_permit_limit_is_rejected()
    {
        var options = new RateLimitingOptions
        {
            ReportsConcurrency = new RateLimitingOptions.ConcurrencyPolicy { PermitLimit = 0, QueueLimit = 0 }
        };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // #311 — the ACCEPTED side of the queue-limit boundary. Paired with the
    // rejection tests below so neither passes with the guard removed: a fixture
    // that only probed the rejected side would still be green if the guard were
    // widened to reject everything.
    [Fact]
    public void Zero_reports_concurrency_queue_limit_is_accepted()
    {
        var options = new RateLimitingOptions
        {
            ReportsConcurrency = new RateLimitingOptions.ConcurrencyPolicy { PermitLimit = 4, QueueLimit = 0 }
        };
        Assert.Null(Record.Exception(options.Validate));
    }

    [Fact]
    public void Negative_reports_concurrency_queue_limit_is_rejected()
    {
        var options = new RateLimitingOptions
        {
            ReportsConcurrency = new RateLimitingOptions.ConcurrencyPolicy { PermitLimit = 4, QueueLimit = -1 }
        };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // #311 — a POSITIVE queue limit must fail the boot too, not just a negative
    // one. The limiter refuses over-cap reports outright (AttemptAcquire, never
    // a waiting acquire), so a queue would never be used; accepting the setting
    // would leave the operator believing requests wait their turn when they are
    // being 429'd. The message has to say so, hence the content assertions.
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void Positive_reports_concurrency_queue_limit_is_rejected(int queueLimit)
    {
        var options = new RateLimitingOptions
        {
            ReportsConcurrency =
                new RateLimitingOptions.ConcurrencyPolicy { PermitLimit = 4, QueueLimit = queueLimit }
        };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("RateLimiting:ReportsConcurrency:QueueLimit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("must be 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("never queued", ex.Message, StringComparison.Ordinal);
    }

    // #311 — the guard is only meaningful if the running limiter really refuses
    // instead of waiting. Asserted here (not only in ReportConcurrencyLimiterTests)
    // so the reason the config value must be 0 is pinned next to the rejection.
    [Fact]
    public void An_over_cap_acquire_is_refused_rather_than_queued()
    {
        var options = new RateLimitingOptions
        {
            ReportsConcurrency = new RateLimitingOptions.ConcurrencyPolicy { PermitLimit = 1, QueueLimit = 0 }
        };
        using var limiter = new ReportConcurrencyLimiter(options);
        var accountId = Guid.NewGuid();

        using var held = limiter.Acquire(accountId);
        using var overCap = limiter.Acquire(accountId);

        Assert.True(held.IsAcquired);
        Assert.False(overCap.IsAcquired);
    }
}
