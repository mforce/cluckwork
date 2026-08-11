namespace Cluckwork.Api.Configuration;

using OpenTelemetry.Exporter;

// OTLP export settings (#214). Bound from the "Otlp" section and validated
// eagerly at boot (repo convention): a malformed protocol or endpoint fails
// startup with a pointed message — never silently, never on the first export.
// Endpoint unset = export disabled (the pre-#214 behavior).
//
// #316 — plaintext HTTP exposes telemetry and Otlp:Headers credentials in
// transit, and a URI carrying userinfo/query/fragment can leak vendor
// credentials or tenant identifiers into console logs or the exception
// message itself. So: userinfo/query/fragment are rejected in EVERY
// environment (never echoed back in a message — they might carry a secret),
// and Production additionally requires https. The one documented escape
// hatch is Otlp:AllowInsecureEndpoint, which mirrors the co-located-stack
// opt-out the Postgres TLS floor already defines (#261/#262,
// Database:AllowInsecureConnection): plaintext is acceptable only when the
// collector is a private peer the traffic never leaves — a loopback collector
// on a dev box, or the sim harness's otel-collector sidecar on its own compose
// network (#243). A real deploy never sets it. Same shape either way:
// allow-list not deny-list, fail closed, one explicitly named opt-out.
public sealed class OtlpOptions
{
    public const string SectionName = "Otlp";

    public string? Endpoint { get; init; }
    public string? Protocol { get; init; }
    public string? Headers { get; init; }
    public bool AllowInsecureEndpoint { get; init; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);

    // What an unset Otlp:Protocol means, and what a one-shot verb falls back to
    // when it degrades to export-disabled (#347) — nothing is exported in that
    // state, so the value only has to be a definite one rather than the right one.
    public const OtlpExportProtocol DefaultProtocol = OtlpExportProtocol.Grpc;

    public OtlpExportProtocol ParseProtocol() => Protocol?.Trim().ToLowerInvariant() switch
    {
        null or "" or "grpc" => DefaultProtocol,
        "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
        var other => throw new InvalidOperationException(
            $"Otlp:Protocol must be 'grpc' or 'http/protobuf', got '{other}'.")
    };

    // The exporter posts to an explicit Endpoint AS-IS — the OTLP spec's
    // per-signal append only happens on the OTEL_* env-var route — so for
    // http/protobuf the signal path is appended here unless already present.
    // isProduction defaults to false so the pure endpoint-shape/append tests
    // (OtlpEndpointResolutionTests) don't need to thread an environment
    // through every call; the real boot path (AddCluckworkTelemetry) always
    // passes it explicitly.
    public Uri ResolveTraceEndpoint(bool isProduction = false) =>
        ResolveSignalEndpoint("/v1/traces", isProduction);

    // #215 — metrics ride the same base endpoint, own signal path.
    public Uri ResolveMetricsEndpoint(bool isProduction = false) =>
        ResolveSignalEndpoint("/v1/metrics", isProduction);

    private static readonly string[] SignalPaths = ["/v1/traces", "/v1/metrics"];

    private Uri ResolveSignalEndpoint(string signalPath, bool isProduction)
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                "Otlp:Endpoint must be an absolute http(s) URI.");

        // #316 — never echo the raw Endpoint value in these three messages: a
        // rejected URI is, by definition, one that might be carrying a secret
        // (userinfo credential, a vendor query param) in exactly the
        // component being rejected.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException(
                "Otlp:Endpoint must not include userinfo (a username/password in the URI). "
                + "Put vendor credentials in Otlp:Headers instead.");

        if (!string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException(
                "Otlp:Endpoint must not include a query string — it can leak tenant/vendor "
                + "parameters into logs and the exported telemetry. Put vendor credentials in "
                + "Otlp:Headers and keep the endpoint a bare base URL.");

        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "Otlp:Endpoint must not include a fragment.");

        if (isProduction && uri.Scheme != Uri.UriSchemeHttps
            && !AllowInsecureEndpoint)
            throw new InvalidOperationException(
                "Production OTLP export requires an https Otlp:Endpoint: plaintext HTTP exposes "
                + "telemetry and Otlp:Headers credentials in transit. If the collector is a "
                + "private peer the traffic never leaves — a loopback collector on a dev box, or "
                + "a sidecar on the same private compose network — set "
                + "Otlp:AllowInsecureEndpoint=true to acknowledge that; a real deploy never "
                + "sets it.");

        if (ParseProtocol() is not OtlpExportProtocol.HttpProtobuf)
            return uri;

        // Both signals share this one endpoint, so a signal-suffixed URL can't
        // be right: '.../v1/traces' would send metrics to /v1/traces/v1/metrics
        // and the collector 404s them silently. Fail at boot instead.
        // The path alone is enough to act on, and is the only part that can be
        // wrong here — echoing the whole endpoint would put userinfo or a query
        // credential in the message. That is currently unreachable only because
        // the checks above throw first, which is an ordering accident, not a
        // property: keep every rejection message non-echoing so a later reorder
        // cannot quietly reopen the leak.
        if (SignalPaths.Any(p => uri.AbsolutePath.TrimEnd('/').EndsWith(p, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Otlp:Endpoint must be the collector's base URL (the app appends per-signal paths itself), "
                + $"but its path '{uri.AbsolutePath}' already names a signal.");

        var builder = new UriBuilder(uri);
        builder.Path = builder.Path.TrimEnd('/') + signalPath;
        return builder.Uri;
    }
}
