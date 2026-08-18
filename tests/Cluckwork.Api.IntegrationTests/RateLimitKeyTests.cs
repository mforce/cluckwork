namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Infrastructure.RateLimiting;

// Unit tests for the rate-limit partition-key derivation (#143). The IP is
// resolved upstream by the framework ForwardedHeaders middleware; this only
// fixes the bucket granularity, and the security-relevant case is that an IPv6
// client can't rotate within its /64 to escape the limit.
public sealed class RateLimitKeyTests
{
    [Fact]
    public void Ipv4_is_keyed_by_the_full_address()
    {
        Assert.Equal("198.51.100.7", RateLimitKey.ForClient(IPAddress.Parse("198.51.100.7")));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_is_normalized_to_ipv4()
    {
        Assert.Equal("198.51.100.7", RateLimitKey.ForClient(IPAddress.Parse("::ffff:198.51.100.7")));
    }

    [Fact]
    public void Ipv6_is_keyed_by_its_64_prefix()
    {
        // Two addresses in the same /64 differing only in the interface id must
        // collapse to one key, or a client rotates addresses to evade the limit.
        var a = RateLimitKey.ForClient(IPAddress.Parse("2001:db8:abcd:1234::1"));
        var b = RateLimitKey.ForClient(IPAddress.Parse("2001:db8:abcd:1234:ffff:ffff:ffff:ffff"));

        Assert.Equal("2001:db8:abcd:1234::/64", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_ipv6_64s_get_different_keys()
    {
        var a = RateLimitKey.ForClient(IPAddress.Parse("2001:db8:abcd:1234::1"));
        var b = RateLimitKey.ForClient(IPAddress.Parse("2001:db8:abcd:9999::1"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Null_client_falls_back_to_the_local_sentinel()
    {
        Assert.Equal("local", RateLimitKey.ForClient(null));
    }
}
