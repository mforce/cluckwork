namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;

internal enum OtlpTransportProfileSource
{
    Canonical,
    Standard,
}

internal sealed record ResolvedOtlpConfiguration(
    OtlpOptions Options,
    OtlpTransportProfileSource Source);

internal static class OtlpConfigurationResolver
{
    internal const string StandardEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    internal const string StandardProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    internal const string StandardHeadersKey = "OTEL_EXPORTER_OTLP_HEADERS";

    private static readonly string[] CanonicalTransportKeys = ["Endpoint", "Protocol", "Headers"];

    public static ResolvedOtlpConfiguration Resolve(IConfiguration configuration)
    {
        var section = configuration.GetSection(OtlpOptions.SectionName);
        var canonical = section.Get<OtlpOptions>() ?? new OtlpOptions();
        var hasCanonicalTransportKey = CanonicalTransportKeys
            .Any(key => section.GetSection(key).Value is not null);

        if (hasCanonicalTransportKey)
            return new(canonical, OtlpTransportProfileSource.Canonical);

        return new(new OtlpOptions
        {
            Endpoint = configuration[StandardEndpointKey],
            Protocol = configuration[StandardProtocolKey],
            Headers = configuration[StandardHeadersKey],
            AllowInsecureEndpoint = canonical.AllowInsecureEndpoint,
        }, OtlpTransportProfileSource.Standard);
    }
}
