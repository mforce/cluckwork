namespace Cluckwork.Api.Configuration;

using OpenTelemetry.Exporter;

// OTLP export settings (#214). Bound from the "Otlp" section and validated
// eagerly at boot (repo convention): a malformed protocol or endpoint fails
// startup with a pointed message — never silently, never on the first export.
// Endpoint unset = export disabled (the pre-#214 behavior).
public sealed class OtlpOptions
{
    public const string SectionName = "Otlp";

    public string? Endpoint { get; init; }
    public string? Protocol { get; init; }
    public string? Headers { get; init; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);

    public OtlpExportProtocol ParseProtocol() => Protocol?.Trim().ToLowerInvariant() switch
    {
        null or "" or "grpc" => OtlpExportProtocol.Grpc,
        "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
        var other => throw new InvalidOperationException(
            $"Otlp:Protocol must be 'grpc' or 'http/protobuf', got '{other}'.")
    };

    // The exporter posts to an explicit Endpoint AS-IS — the OTLP spec's
    // "/v1/traces" append only happens on the OTEL_* env-var route — so for
    // http/protobuf the signal path is appended here unless already present.
    public Uri ResolveTraceEndpoint()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"Otlp:Endpoint must be an absolute http(s) URI, got '{Endpoint}'.");

        if (ParseProtocol() is not OtlpExportProtocol.HttpProtobuf
            || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/traces", StringComparison.Ordinal))
            return uri;

        var builder = new UriBuilder(uri);
        builder.Path = builder.Path.TrimEnd('/') + "/v1/traces";
        return builder.Uri;
    }
}
