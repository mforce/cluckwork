namespace Cluckwork.Infrastructure.RateLimiting;

using System.Net;
using System.Net.Sockets;

// Derives the rate-limit partition key from the resolved client IP. The IP
// itself is resolved by the framework ForwardedHeaders middleware; this only
// decides the bucket granularity.
//
// #544 — moved from Cluckwork.Api.RateLimiting to Cluckwork.Infrastructure so the
// distributed limiter policy (DistributedIpFixedWindowPolicy, same assembly as the
// internal IFixedWindowCounter port) can share this one canonical derivation. The Api
// OnRejected handler still uses it for the security-event ClientIp field; it now
// references it from here. One copy, never two — the IPv6-/64 collapse is a security
// control and a second drifting copy would be a hole.
public static class RateLimitKey
{
    // A single residential IPv6 customer controls a whole /64, so keying by the
    // full /128 would let them rotate addresses to evade the limit. Bucket IPv6
    // by its /64 prefix; IPv4 is keyed by the full address.
    public static string ForClient(IPAddress? clientIp)
    {
        if (clientIp is null)
            return "local"; // no socket peer — only the in-process test host

        if (clientIp.IsIPv4MappedToIPv6)
            clientIp = clientIp.MapToIPv4();

        if (clientIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = clientIp.GetAddressBytes();
            Array.Clear(bytes, 8, 8); // zero the interface identifier → /64 prefix
            return $"{new IPAddress(bytes)}/64";
        }

        return clientIp.ToString();
    }
}
