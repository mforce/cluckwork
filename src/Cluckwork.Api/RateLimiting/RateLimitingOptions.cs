namespace Cluckwork.Api.RateLimiting;

using System.Net;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    // Endpoint policy names used by RequireRateLimiting in AuthEndpoints.
    // Login and refresh are limited separately: login guards password spraying
    // (strict); refresh guards a high-entropy token and also carries legitimate
    // automatic session traffic, so it is looser — otherwise several users
    // behind one NAT IP starve the shared budget with normal refreshes (#143).
    public const string LoginPolicyName = "auth-login";
    public const string RefreshPolicyName = "auth-refresh";
    // #217: the anonymous browser error-report endpoint. The budget guards the
    // LOG, not a credential — enough for a genuinely crashing screen to get its
    // story out, too little to flood the log from one address.
    public const string ClientErrorsPolicyName = "client-errors";

    public FixedWindow Login { get; init; } = new() { PermitLimit = 10, WindowSeconds = 900 };
    public FixedWindow Refresh { get; init; } = new() { PermitLimit = 60, WindowSeconds = 900 };
    public FixedWindow ClientErrors { get; init; } = new() { PermitLimit = 10, WindowSeconds = 300 };

    // Reverse-proxy networks whose X-Forwarded-For is honored (CIDR; /32 for a
    // single address). Fed to the framework ForwardedHeaders middleware, which
    // resolves the real client IP; the limiter never parses XFF itself.
    public string[] TrustedProxies { get; init; } = [];

    public sealed class FixedWindow
    {
        public int PermitLimit { get; init; }
        public int WindowSeconds { get; init; }
    }

    public IPNetwork[] ParseTrustedProxies() =>
        [.. TrustedProxies.Select(IPNetwork.Parse)];

    // Fail fast at boot rather than throwing inside the partition factory on the
    // first auth request (which would surface as a 500 on the login path).
    public void Validate()
    {
        ValidateWindow(nameof(Login), Login);
        ValidateWindow(nameof(Refresh), Refresh);
        ValidateWindow(nameof(ClientErrors), ClientErrors);
        ParseTrustedProxies(); // throws FormatException on a bad CIDR
    }

    private static void ValidateWindow(string name, FixedWindow window)
    {
        if (window.PermitLimit <= 0)
            throw new InvalidOperationException(
                $"RateLimiting:{name}:PermitLimit must be greater than 0.");
        if (window.WindowSeconds <= 0)
            throw new InvalidOperationException(
                $"RateLimiting:{name}:WindowSeconds must be greater than 0.");
    }
}
