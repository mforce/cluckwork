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

        // Must be 0 — enforced by ValidateConcurrency. The report cap refuses
        // over-cap work outright (429 + Retry-After) instead of parking it in a
        // queue, so there is no waiting acquire for a queue to feed. The setting
        // is kept, rather than dropped, precisely so it can be REJECTED: config
        // binding ignores keys with no matching property, so removing it would
        // turn "RateLimiting:ReportsConcurrency:QueueLimit=5" back into the
        // silent no-op this guard exists to prevent.
        public int QueueLimit { get; init; }
    }

    // #347 review — a malformed entry used to surface as a bare
    // `FormatException: An invalid IP network was specified.` from deep inside
    // IPNetwork.Parse, naming neither the setting nor the value, and (being a
    // FormatException) it also slipped past the role filter that spares the
    // one-shot verbs. Both problems are the same fix: fail as a named
    // InvalidOperationException, like every other configuration guard here.
    public IPNetwork[] ParseTrustedProxies()
    {
        var networks = new IPNetwork[TrustedProxies.Length];
        for (var i = 0; i < TrustedProxies.Length; i++)
        {
            var entry = TrustedProxies[i];
            if (!IPNetwork.TryParse(entry, out networks[i]))
                throw new InvalidOperationException(
                    $"RateLimiting:TrustedProxies[{i}] is not a valid CIDR network: '{entry}'. "
                    + "Use an address and prefix length, e.g. '10.0.0.0/8' or '2001:db8::/32'.");
        }

        return networks;
    }

    // Fail fast at boot rather than throwing inside the partition factory on the
    // first auth request (which would surface as a 500 on the login path).
    public void Validate()
    {
        ValidateWindow(nameof(Login), Login);
        ValidateWindow(nameof(Refresh), Refresh);
        ValidateWindow(nameof(ClientErrors), ClientErrors);
        ValidateConcurrency(nameof(ReportsConcurrency), ReportsConcurrency);
        ParseTrustedProxies(); // throws a named InvalidOperationException on a bad CIDR
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
        // Fail the boot on ANY nonzero queue limit, not just a negative one:
        // over-cap report requests are refused immediately with 429 +
        // Retry-After and are never queued, so a queue limit could only ever be
        // an inert setting that misleads whoever set it into thinking requests
        // wait their turn. Same fail-closed stance as the #260 trusted-proxy and
        // #261/#262 Postgres TLS boot guards: an unusable setting stops the
        // process rather than running degraded.
        if (policy.QueueLimit != 0)
            throw new InvalidOperationException(
                $"RateLimiting:{name}:QueueLimit must be 0 (was {policy.QueueLimit}). " +
                "Report requests over the concurrency cap are refused with HTTP 429 and a " +
                "Retry-After header — they are never queued — so a nonzero queue limit " +
                "would have no effect.");
    }
}
