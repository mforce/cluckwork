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
    // #311: caps concurrently in-flight report queries per ACCOUNT (not IP —
    // see ReportConcurrencyLimiter for why this can't ride the same
    // IP-keyed RequireRateLimiting pipeline as the policies above).
    public const string ReportsConcurrencyPolicyName = "reports-concurrency";

    public FixedWindow Login { get; init; } = new() { PermitLimit = 10, WindowSeconds = 900 };
    public FixedWindow Refresh { get; init; } = new() { PermitLimit = 60, WindowSeconds = 900 };
    public FixedWindow ClientErrors { get; init; } = new() { PermitLimit = 10, WindowSeconds = 300 };
    // Small on purpose: a report query is a bounded-range aggregate (#311), not
    // a hot path — a genuine user rarely has more than one or two in flight at
    // once (e.g. a dashboard firing production+sales+expenses+profit together).
    public ConcurrencyPolicy ReportsConcurrency { get; init; } = new() { PermitLimit = 4, QueueLimit = 0 };

    // Reverse-proxy networks whose X-Forwarded-For is honored (CIDR; /32 for a
    // single address). Fed to the framework ForwardedHeaders middleware, which
    // resolves the real client IP; the limiter never parses XFF itself.
    public string[] TrustedProxies { get; init; } = [];

    // Opt-out for the #260 Production boot guard. Leaving TrustedProxies empty in
    // Production normally fails the boot: with no trusted proxy the forwarded
    // headers are ignored, so HSTS (#144) never sees the real HTTPS scheme and the
    // per-IP login limiter (#143) collapses to one global bucket. Set true ONLY for
    // a deliberate direct-TLS-exposure deploy (the app terminates TLS itself, no
    // fronting proxy) to acknowledge that trade-off and boot anyway.
    public bool AllowNoTrustedProxies { get; init; }

    public sealed class FixedWindow
    {
        public int PermitLimit { get; init; }
        public int WindowSeconds { get; init; }
    }

    public sealed class ConcurrencyPolicy
    {
        public int PermitLimit { get; init; }
        public int QueueLimit { get; init; }
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
        ValidateConcurrency(nameof(ReportsConcurrency), ReportsConcurrency);
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

    private static void ValidateConcurrency(string name, ConcurrencyPolicy policy)
    {
        if (policy.PermitLimit <= 0)
            throw new InvalidOperationException(
                $"RateLimiting:{name}:PermitLimit must be greater than 0.");
        if (policy.QueueLimit < 0)
            throw new InvalidOperationException(
                $"RateLimiting:{name}:QueueLimit must not be negative.");
    }
}
