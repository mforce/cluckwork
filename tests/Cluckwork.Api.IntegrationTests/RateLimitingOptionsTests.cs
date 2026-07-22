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
}
