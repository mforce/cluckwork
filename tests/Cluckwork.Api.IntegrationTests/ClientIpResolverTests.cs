namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.RateLimiting;

// Pure unit tests for the rate-limit partition key resolver (#143). The
// spoofing rules matter more than the happy path: X-Forwarded-For is only
// honored when the socket peer is a trusted proxy (or the in-process test
// server, which has no socket address at all).
public sealed class ClientIpResolverTests
{
    private static IPNetwork[] Trusted(params string[] cidrs) =>
        [.. cidrs.Select(IPNetwork.Parse)];

    [Fact]
    public void Untrusted_remote_ignores_forwarded_header()
    {
        var key = ClientIp.Resolve(
            IPAddress.Parse("198.51.100.7"), "203.0.113.1", Trusted("172.16.0.0/12"));

        Assert.Equal("198.51.100.7", key);
    }

    [Fact]
    public void Trusted_proxy_remote_uses_rightmost_untrusted_forwarded_entry()
    {
        // client, intermediate-proxy — the rightmost entry not in the trusted
        // set is the client as seen by our outermost trusted proxy.
        var key = ClientIp.Resolve(
            IPAddress.Parse("172.18.0.2"),
            "203.0.113.1, 172.18.0.9",
            Trusted("172.16.0.0/12"));

        Assert.Equal("203.0.113.1", key);
    }

    [Fact]
    public void Loopback_remote_is_trusted_implicitly()
    {
        var key = ClientIp.Resolve(
            IPAddress.Loopback, "203.0.113.5", Trusted());

        Assert.Equal("203.0.113.5", key);
    }

    [Fact]
    public void Null_remote_honors_forwarded_header()
    {
        // In-process TestServer requests carry no socket address.
        var key = ClientIp.Resolve(null, "203.0.113.9", Trusted());

        Assert.Equal("203.0.113.9", key);
    }

    [Fact]
    public void Null_remote_without_forwarded_header_falls_back_to_local_sentinel()
    {
        var key = ClientIp.Resolve(null, null, Trusted());

        Assert.Equal("local", key);
    }

    [Fact]
    public void Trusted_remote_with_all_entries_trusted_falls_back_to_remote()
    {
        var key = ClientIp.Resolve(
            IPAddress.Parse("172.18.0.2"), "172.18.0.9", Trusted("172.16.0.0/12"));

        Assert.Equal("172.18.0.2", key);
    }

    [Fact]
    public void Invalid_forwarded_entries_are_skipped()
    {
        var key = ClientIp.Resolve(
            IPAddress.Parse("172.18.0.2"),
            "203.0.113.1, not-an-ip",
            Trusted("172.16.0.0/12"));

        Assert.Equal("203.0.113.1", key);
    }

    [Fact]
    public void Ipv4_mapped_ipv6_remote_is_normalized_for_matching_and_key()
    {
        var key = ClientIp.Resolve(
            IPAddress.Parse("::ffff:198.51.100.7"), null, Trusted("172.16.0.0/12"));

        Assert.Equal("198.51.100.7", key);
    }
}
