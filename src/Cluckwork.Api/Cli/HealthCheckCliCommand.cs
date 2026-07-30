namespace Cluckwork.Api.Cli;

// `healthcheck` (#266) — the container HEALTHCHECK probe. The hardened runtime
// image ships no curl/wget (#267) — only the .NET runtime — so the probe rides
// the same binary: GET the SERVING process's /health/ready over loopback and
// map the result to an exit code (0 = ready, 1 = not / unreachable). An
// orchestrator or `docker`/compose then stops routing to an instance whose
// /health/ready is 503 (DB down, migrations pending).
//
// Unlike migrate/seed/recover-admin — which operate ON the built host and so go
// through CliDispatcher AFTER Build() — this verb needs no host, DI, DB or
// config: it only makes one HTTP call to the already-running server. So
// Program.cs dispatches it BEFORE building the host (a 30s HEALTHCHECK must not
// re-run the whole app startup, re-validate config, and re-log warnings on every
// tick). Kept here in Cli/ as a testable unit — ProbeAsync/DefaultReadyUrl are
// exercised directly, no Docker.
public static class HealthCheckCliCommand
{
    // The verb Program.cs matches on args[0] before host construction.
    public const string Verb = "healthcheck";

    // Kept short: a HEALTHCHECK that hangs is worse than one that reports
    // unhealthy — Docker's own --timeout (5s) bounds it too; this is the inner
    // guard so a slow/half-open socket can't wedge the probe process.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    public static Task<int> RunAsync(string[] args)
    {
        var url = ArgValue(args, "--url") ?? DefaultReadyUrl();
        return ProbeAsync(url, ProbeTimeout);
    }

    // Default target: the loopback readiness endpoint on the port Kestrel binds.
    // The probe always speaks plain HTTP over loopback (the container serves HTTP
    // there; TLS terminates at the edge). Resolve the bound HTTP port in the same
    // precedence ASP.NET Core itself uses:
    //   1. ASPNETCORE_URLS (compose sets `http://+:8080`), first http:// entry;
    //   2. ASPNETCORE_HTTP_PORTS — the .NET 8+ mechanism the aspnet base image
    //      uses (`ASPNETCORE_HTTP_PORTS=8080`), a ';'-separated bare-port list,
    //      which is what's in effect when nothing sets ASPNETCORE_URLS;
    //   3. 8080 — the EXPOSE/compose contract.
    // So re-porting the container the base-image-idiomatic way still resolves.
    // `--url` overrides everything (used by tests).
    internal static string DefaultReadyUrl(string? aspnetcoreUrls = null, string? aspnetcoreHttpPorts = null)
    {
        aspnetcoreUrls ??= Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        aspnetcoreHttpPorts ??= Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var port = FirstHttpPort(aspnetcoreUrls) ?? FirstBarePort(aspnetcoreHttpPorts) ?? 8080;
        return $"http://localhost:{port}/health/ready";
    }

    // ASPNETCORE_HTTP_PORTS is a ';'-separated list of bare port numbers
    // (e.g. `8080` or `8080;8081`) — no scheme/host. Returns the first valid one.
    internal static int? FirstBarePort(string? httpPorts)
    {
        if (string.IsNullOrWhiteSpace(httpPorts))
            return null;

        foreach (var entry in httpPorts.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(entry, out var port) && port is > 0 and <= 65535)
                return port;
        return null;
    }

    // Pulls the port from the first http:// entry of an ASPNETCORE_URLS string
    // (`http://+:8080`, `http://0.0.0.0:5000;https://...`, `http://localhost`).
    // The container serves plain HTTP on loopback (TLS terminates at the edge),
    // so https:// entries are skipped. Kestrel binds a wildcard host (`+`/`*`)
    // that `Uri` can't parse, so normalize it to `localhost` purely to read the
    // port — we probe localhost regardless. `Uri.Port` yields the explicit port
    // or the scheme default (80 for http) when omitted. Null → caller falls back.
    internal static int? FirstHttpPort(string? aspnetcoreUrls)
    {
        if (string.IsNullOrWhiteSpace(aspnetcoreUrls))
            return null;

        foreach (var entry in aspnetcoreUrls.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = entry
                .Replace("://+", "://localhost", StringComparison.Ordinal)
                .Replace("://*", "://localhost", StringComparison.Ordinal);
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp)
                return uri.Port;
        }
        return null;
    }

    // 2xx → 0 (ready). Any other status, OR an inability to reach the endpoint
    // (connection refused, DNS failure, timeout — all surface as an exception
    // here), → 1: a probe that cannot obtain a healthy answer reports UNHEALTHY,
    // never a false green. Output goes to stderr (the container health log), the
    // only sink available this early — no host, so no ILogger.
    internal static async Task<int> ProbeAsync(string url, TimeSpan timeout)
    {
        try
        {
            // A health probe must talk to the endpoint DIRECTLY: no redirect
            // following (a 3xx away from /health/ready would otherwise let the SPA
            // fallback's 200 masquerade as ready — a false green) and no proxy (a
            // stray HTTP_PROXY/ALL_PROXY in the container env would otherwise route
            // this loopback GET through a proxy — a proxy 200 = false green, an
            // unreachable proxy = false red).
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
            };
            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
            using var response = await http.GetAsync(url);
            if (response.IsSuccessStatusCode)
                return 0;

            await Console.Error.WriteLineAsync(
                $"Healthcheck: {url} returned HTTP {(int)response.StatusCode}.");
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Healthcheck: {url} unreachable: {ex.Message}");
            return 1;
        }
    }

    // Tiny `--flag value` lookup (mirrors CliDispatcher.ArgValue; kept local so
    // this verb has zero dependency on the post-Build dispatcher it doesn't use).
    private static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag)
                return args[i + 1];
        return null;
    }
}
