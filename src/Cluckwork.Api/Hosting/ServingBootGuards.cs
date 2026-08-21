namespace Cluckwork.Api.Hosting;

// #347 — the Production boot guards that protect the SERVING process's security
// posture, gathered behind one role-checked entry point.
//
// Both of these used to sit inline in Program.cs BELOW the CLI dispatcher's
// return, and that position was the whole of what stopped them aborting
// `migrate`/`seed`/`recover-admin`/`bootstrap-admin`/`list-accounts`/
// `suspend-account`/`reactivate-account`/`provision-account`. Program.cs now calls this
// BEFORE the dispatch, deliberately: a serving-only guard sitting ahead of the
// dispatcher and still not touching the one-shot verbs is the #331 failure mode
// disarmed rather than merely avoided. Move this call again and nothing breaks.
internal static class ServingBootGuards
{
    // Gated on IsProduction() (not !IsDevelopment()) deliberately: the
    // integration Testing environment is also empty-proxied and wildcard-hosted
    // and must still boot; a real Staging serving environment, if ever
    // introduced, would be added to this gate.
    public static void EnsureServingConfiguration(
        ProcessRole role,
        IHostEnvironment environment,
        IConfiguration configuration,
        CluckworkRateLimitingRegistration rateLimiting)
    {
        if (role is not ProcessRole.Serving || !environment.IsProduction())
            return;

        EnsureTrustedProxiesConfigured(rateLimiting);
        EnsureAllowedHostsPinned(configuration);
    }

    // #260 — the forwarded-headers middleware honours X-Forwarded-Proto/-For
    // solely from the trustedProxies networks; with that list empty in
    // Production two controls silently go inert: HSTS (#144) never sees the real
    // HTTPS scheme and stops emitting, and the per-IP login rate limiter (#143)
    // collapses to one global bucket (every request looks like it came from the
    // proxy hop). Fail the boot loudly rather than run degraded.
    private static void EnsureTrustedProxiesConfigured(CluckworkRateLimitingRegistration rateLimiting)
    {
        if (rateLimiting.TrustedProxies.Length != 0 || rateLimiting.Options.AllowNoTrustedProxies)
            return;

        throw new InvalidOperationException(
            "RateLimiting:TrustedProxies is empty in Production, so the app trusts no "
            + "proxy's X-Forwarded-* headers. Two security controls then silently go "
            + "inert: HSTS (#144) never sees the real HTTPS scheme and stops emitting, "
            + "and the per-IP login rate limiter (#143) collapses to a single global "
            + "bucket. Fix ONE of: (1) set RateLimiting:TrustedProxies to the edge "
            + "proxy/load-balancer network CIDR (the hop that terminates TLS and adds "
            + "X-Forwarded-*); or (2) for a rare deploy that terminates TLS itself with "
            + "no fronting proxy, set RateLimiting:AllowNoTrustedProxies=true to "
            + "acknowledge the direct-exposure trade-off and boot anyway.");
    }

    // #319 — appsettings.json defaults AllowedHosts to "*"; a deploy that omits
    // or misnames the host variable (a blank ${CLUCKWORK_HOST} substitution was
    // observed) then silently disables Host-header filtering (#144) and a forged
    // Host header is accepted. Fail the boot loudly unless a concrete public host
    // is pinned. Loopback is force-added for health probes later
    // (AddCluckworkEdgeSecurity), so it need not appear in config here.
    private static void EnsureAllowedHostsPinned(IConfiguration configuration)
    {
        var configuredHosts = (configuration["AllowedHosts"] ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasConcretePublicHost = configuredHosts.Length > 0 && configuredHosts.All(h => h != "*");
        if (hasConcretePublicHost)
            return;

        throw new InvalidOperationException(
            "AllowedHosts is missing, blank, or wildcard ('*') in Production, so Host-header "
            + "filtering (#144) is off and a forged Host header is accepted. Set AllowedHosts to "
            + "the concrete public hostname the app serves (the deployment supplies it as "
            + "CLUCKWORK_HOST). Loopback (localhost/127.0.0.1/[::1]) is always allowed for "
            + "container health probes, so it need not be listed.");
    }
}
