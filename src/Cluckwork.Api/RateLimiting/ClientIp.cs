namespace Cluckwork.Api.RateLimiting;

using System.Net;

// Resolves the rate-limit partition key for a request: the real client IP.
// Behind the reverse proxy the socket address is the proxy, so X-Forwarded-For
// must be honored — but ONLY when the socket peer is a trusted proxy, otherwise
// any direct caller could spoof the header and dodge its own bucket (#143).
public static class ClientIp
{
    public static string Resolve(
        IPAddress? remoteIp,
        string? forwardedFor,
        IReadOnlyList<IPNetwork> trustedProxies)
    {
        var remote = Normalize(remoteIp);

        if (remote is not null && !IsTrusted(remote, trustedProxies))
            return remote.ToString();

        // Socket peer is a trusted proxy (or absent — in-process test server):
        // walk X-Forwarded-For right-to-left; the rightmost untrusted entry is
        // the client as seen by our outermost trusted proxy. Leftward entries
        // are client-supplied and spoofable.
        if (forwardedFor is not null)
        {
            var entries = forwardedFor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                if (!IPAddress.TryParse(entries[i], out var hop))
                    continue;
                hop = Normalize(hop)!;
                if (!IsTrusted(hop, trustedProxies))
                    return hop.ToString();
            }
        }

        return remote?.ToString() ?? "local";
    }

    private static bool IsTrusted(IPAddress ip, IReadOnlyList<IPNetwork> trustedProxies) =>
        IPAddress.IsLoopback(ip) || trustedProxies.Any(n => n.Contains(ip));

    private static IPAddress? Normalize(IPAddress? ip) =>
        ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
}
