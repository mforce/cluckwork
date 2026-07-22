namespace Cluckwork.Api.RateLimiting;

using System.Net;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    // Endpoint policy name used by RequireRateLimiting on the auth group.
    public const string AuthPolicyName = "auth";

    public AuthLimit Auth { get; init; } = new();

    // CIDR notation (use /32 for a single address). Requests whose socket peer
    // is in one of these networks get their client IP from X-Forwarded-For.
    public string[] TrustedProxies { get; init; } = [];

    public sealed class AuthLimit
    {
        public int PermitLimit { get; init; } = 10;
        public int WindowSeconds { get; init; } = 900;
    }

    public IPNetwork[] ParseTrustedProxies() =>
        [.. TrustedProxies.Select(IPNetwork.Parse)];
}
