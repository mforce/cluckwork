namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

// #840 — the login rate-limit bucket is keyed on the client address ALONE
// (RateLimitKey.ForClient, reached from DistributedIpFixedWindowPolicy), and every
// test host in this suite is reached over loopback. So every class that exercises
// the login policy shares ONE bucket, and whichever runs last sees a 429 where it
// expected a 401.
//
// The probe on #866 measured it instead of guessing: a fourteen-request burst moved
// the shared counter by forty. 40 = 14 + 30, and 30 = 3 classes x 10 — three
// neighbours each draining the whole budget while the probe believed it was
// spending its own.
//
// This gives a class its own bucket by declaring a trusted proxy for that host, so
// the framework's ForwardedHeaders middleware honours X-Forwarded-For and the
// limiter keys on the address the test names instead of on the socket peer.
//
// It goes through CONFIGURATION and not PostConfigure<ForwardedHeadersOptions> for a
// reason worth keeping: Program.cs reads RateLimiting:TrustedProxies eagerly into a
// registration record and hands that snapshot to AddCluckworkEdgeSecurity, which
// clears KnownIPNetworks and rebuilds them from it. A PostConfigure runs afterwards
// and would be silently overwritten — a helper written that way looks correct and
// does nothing at runtime.
public static class IsolatedLoginBucket
{
    // The address a host reports as the client for traffic carrying no
    // X-Forwarded-For of its own — which is every login issued through the
    // factory's helpers, the ~477 call sites that never touch a rate-limit header
    // and were the traffic quietly draining the loopback bucket.
    //
    // It is SYNTHESIZED, and that is both the whole mechanism and its whole limit.
    // The real peer is loopback, so the only address the middleware can adopt is one
    // the test writes into the header. Legitimate for a test's own bucket; in
    // production, adopting an address no proxy attested to is exactly the spoof #143
    // exists to prevent. Which is why the guard below refuses to do this for a host
    // that declares its own proxies.
    public const string DefaultClientIp = "198.51.100.2";

    // The peer FakeRemoteIpStartupFilter hosts report. Loopback is the genuine peer
    // everywhere else — TestServer reports it and Kestrel binds to it — so both are
    // listed and one helper serves the in-process and real-socket shapes alike.
    public const string SynthesizedProxy = "198.51.100.1";

    // Opts this host's login traffic out of the shared loopback bucket.
    //
    // The trusted-proxy list is what makes the forwarded header honoured at all, so
    // it is the one thing this has to write. It refuses rather than merges: a host
    // that already configured its own proxies is exercising the trust decision
    // itself, and overwriting that list would delete the thing under test while
    // leaving the test green.
    public static void ConfigureIsolatedLoginBucket(this IWebHostBuilder builder, string hostName)
    {
        if (builder.GetSetting("RateLimiting:TrustedProxies:0") is { } existing)
        {
            throw new InvalidOperationException(
                $"{hostName} already sets RateLimiting:TrustedProxies:0 ({existing}). A host that "
                + "configures its own trusted proxies is exercising the forwarded-header trust "
                + "decision (#143's anti-spoof boundary); isolating its login bucket would "
                + "overwrite the proxy list that decision is made of. Such a host already chooses "
                + "its own client address — isolate it that way, or share the loopback bucket "
                + "deliberately.");
        }

        builder.UseSetting("RateLimiting:TrustedProxies:0", "127.0.0.1/32");
        builder.UseSetting("RateLimiting:TrustedProxies:1", $"{SynthesizedProxy}/32");
    }

    // A client in this host's default bucket. The header has to ride on the request,
    // so opting in is two steps: the host, then its clients. Building the client here
    // rather than at each call site is what keeps TestHarness.LoginAsync's internally
    // created clients in the same bucket as the test's own.
    public static HttpClient CreateIsolatedClient(
        this CluckworkWebApplicationFactory factory, string? clientIp = null)
    {
        // The parameterless CreateClient() is the only overload that rewrites the
        // base address to the bound Kestrel port under UseKestrel(0), so an options
        // object has to copy it — same reason as TestHarness.Cookieless.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            BaseAddress = factory.ClientOptions.BaseAddress,
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientIp ?? DefaultClientIp);
        return client;
    }

    // A bucket of its own, for a test that needs several independent clients — the
    // per-IP-partition assertions.
    //
    // It is a TEST-NET-3 address, and that is load-bearing rather than tidiness.
    // ForwardedHeadersMiddleware adopts a forwarded address only if it is a valid
    // literal; an invalid one is dropped and the peer's own address is kept. That
    // silently collapses every "independent" bucket back onto loopback, where the
    // test's own exhaustion turns the next client's first request into a 429 — which
    // is exactly how an earlier version of this helper failed, and it looked like the
    // isolation not working rather than like a bad address. TEST-NET-3 is guaranteed
    // routable-invalid by RFC 5737, so it cannot collide with a real deployment's
    // proxy or client space and cannot be rejected as malformed.
    //
    // The dotted-quad form is also why this returns a string and not an IPAddress:
    // an IPv4 address cannot carry five octets, and the first cut of this helper
    // produced "198.51.100.121.215" by dividing a slot into two octets.
    public static string ClientIpFor(string callerName)
    {
        var hash = 2166136261u; // FNV-1a, not String.GetHashCode: that is randomized
                                // per process, so a bucket derived from it would move
                                // between runs and a flake would reproduce only on the
                                // machine that first saw it.
        foreach (var c in callerName)
        {
            hash = (hash ^ c) * 16777619u;
        }

        // TEST-NET-3 is a /24 and the limiter keys IPv4 by the whole address, so this
        // has 253 usable slots (.0 network, .1 SynthesizedProxy, .2 DefaultClientIp).
        // A collision is not assumed away: two classes sharing a slot share a bucket,
        // which is the status quo this file exists to remove rather than a new failure
        // mode. It is checked, not counted on — see the guard in this assembly.
        var slot = hash % 253u + 3u;
        return $"198.51.100.{slot}";
    }
}
