namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Configuration;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;

public sealed class OtlpConfigurationResolverTests
{
    [Theory]
    [InlineData("Otlp:Endpoint", "", "", null, null)]
    [InlineData("Otlp:Protocol", "grpc", null, "grpc", null)]
    [InlineData("Otlp:Headers", "Authorization=test", null, null, "Authorization=test")]
    public void Any_canonical_transport_key_selects_the_complete_canonical_profile(
        string canonicalKey,
        string canonicalValue,
        string? expectedEndpoint,
        string? expectedProtocol,
        string? expectedHeaders)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [canonicalKey] = canonicalValue,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://standard.example:4317",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=standard-secret",
        });

        var resolved = OtlpConfigurationResolver.Resolve(configuration);

        Assert.Equal(OtlpTransportProfileSource.Canonical, resolved.Source);
        Assert.False(resolved.Options.Enabled);
        Assert.Equal(expectedEndpoint, resolved.Options.Endpoint);
        Assert.Equal(expectedProtocol, resolved.Options.Protocol);
        Assert.Equal(expectedHeaders, resolved.Options.Headers);
    }

    [Fact]
    public void No_canonical_transport_key_maps_all_standard_values_together()
    {
        var resolved = OtlpConfigurationResolver.Resolve(BuildConfiguration(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://standard.example:4317",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=standard-secret",
        }));

        Assert.Equal(OtlpTransportProfileSource.Standard, resolved.Source);
        Assert.Equal("https://standard.example:4317", resolved.Options.Endpoint);
        Assert.Equal("http/protobuf", resolved.Options.Protocol);
        Assert.Equal("x-otlp-api-key=standard-secret", resolved.Options.Headers);
    }

    [Fact]
    public void Allow_insecure_endpoint_applies_to_standard_profile_without_selecting_canonical_profile()
    {
        var resolved = OtlpConfigurationResolver.Resolve(BuildConfiguration(new Dictionary<string, string?>
        {
            ["Otlp:AllowInsecureEndpoint"] = "true",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://standard.example:4317",
        }));

        Assert.Equal(OtlpTransportProfileSource.Standard, resolved.Source);
        Assert.True(resolved.Options.AllowInsecureEndpoint);
        Assert.Equal("http://standard.example:4317", resolved.Options.Endpoint);
    }

    [Fact]
    public void Blank_canonical_endpoint_disables_export_despite_a_standard_endpoint()
    {
        var resolved = OtlpConfigurationResolver.Resolve(BuildConfiguration(new Dictionary<string, string?>
        {
            ["Otlp:Endpoint"] = "",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://standard.example:4317",
        }));

        Assert.Equal(OtlpTransportProfileSource.Canonical, resolved.Source);
        Assert.False(resolved.Options.Enabled);
        Assert.Equal("", resolved.Options.Endpoint);
    }

    [Fact]
    public void Canonical_endpoint_only_does_not_inherit_standard_protocol_or_headers()
    {
        var resolved = OtlpConfigurationResolver.Resolve(BuildConfiguration(new Dictionary<string, string?>
        {
            ["Otlp:Endpoint"] = "https://canonical.example:4317",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=standard-secret",
        }));

        Assert.Equal(OtlpTransportProfileSource.Canonical, resolved.Source);
        Assert.Equal("https://canonical.example:4317", resolved.Options.Endpoint);
        Assert.Null(resolved.Options.Protocol);
        Assert.Null(resolved.Options.Headers);
    }

    [Fact]
    public void Missing_settings_return_a_disabled_standard_profile_with_the_default_protocol()
    {
        var resolved = OtlpConfigurationResolver.Resolve(BuildConfiguration(new Dictionary<string, string?>()));

        Assert.Equal(OtlpTransportProfileSource.Standard, resolved.Source);
        Assert.False(resolved.Options.Enabled);
        Assert.Equal(OtlpOptions.DefaultProtocol, resolved.Options.ParseProtocol());
    }

    private static IConfiguration BuildConfiguration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

// The shared factory explicitly selects the canonical disabled profile. These
// cases exercise only validation that rejects before an enabled exporter host
// exists; valid exporting coverage belongs to the isolated child-process suite.
public sealed class OtlpExporterHostValidationTests(CluckworkWebApplicationFactory factory)
    : IClassFixture<CluckworkWebApplicationFactory>
{
    [Fact]
    public void Malformed_otlp_endpoint_fails_at_startup_with_a_pointed_message()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Endpoint", "not a uri"));

        var exception = Record.Exception(() => broken.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Otlp:Endpoint", exception!.Message);
    }

    [Fact]
    public void Non_http_scheme_endpoint_fails_at_startup_with_a_pointed_message()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Endpoint", "localhost:4317"));

        var exception = Record.Exception(() => broken.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Otlp:Endpoint", exception!.Message);
    }

    [Fact]
    public void Unknown_otlp_protocol_fails_at_startup_with_a_pointed_message()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Protocol", "carrier-pigeon"));

        var exception = Record.Exception(() => broken.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Otlp:Protocol", exception!.Message);
    }

    [Fact]
    public void Unknown_otlp_protocol_fails_at_startup_even_with_export_disabled()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otlp:Endpoint", "");
            builder.UseSetting("Otlp:Protocol", "carrier-pigeon");
        });

        var exception = Record.Exception(() => broken.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Otlp:Protocol", exception!.Message);
    }

    [Fact]
    public async Task Disabled_otlp_endpoint_boots_without_an_exporter()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
    }
}

// Pure endpoint-resolution rules (unit-level, no host): the exporter uses an
// explicit Endpoint AS-IS, so the app owns the /v1/traces append for
// http/protobuf — including the edges reviewers flagged (trailing slashes,
// vendor base paths). #316 — a query string used to ride along as a "vendor
// base path" edge case; it is now rejected outright (see
// Endpoint_with_query_string_fails_in_every_environment below) because it can
// carry tenant/vendor identifiers into logs.
public sealed class OtlpEndpointResolutionTests
{
    // Both signals resolve from ONE base endpoint — asserting them together
    // keeps coverage symmetric and the append rules in lock-step.
    [Theory]
    [InlineData("http://collector:4318", "http://collector:4318/v1/traces", "http://collector:4318/v1/metrics")]
    [InlineData("http://collector:4318/", "http://collector:4318/v1/traces", "http://collector:4318/v1/metrics")]
    [InlineData("https://host/otlp", "https://host/otlp/v1/traces", "https://host/otlp/v1/metrics")]
    public void Http_protobuf_appends_each_signal_path_to_the_base(
        string given, string traces, string metrics)
    {
        var options = new OtlpOptions { Endpoint = given, Protocol = "http/protobuf" };
        Assert.Equal(new Uri(traces), options.ResolveTraceEndpoint());
        Assert.Equal(new Uri(metrics), options.ResolveMetricsEndpoint());
    }

    // With two signals on one endpoint, a signal-suffixed URL cannot be right:
    // '.../v1/traces' would send metrics to /v1/traces/v1/metrics — a silent
    // 404 at the collector. Reject at boot with the base-URL instruction
    // instead (agent review of #227; repo never-silent convention).
    [Theory]
    [InlineData("http://collector:4318/v1/traces")]
    [InlineData("http://collector:4318/v1/traces/")]
    [InlineData("http://collector:4318/v1/metrics")]
    [InlineData("https://host/otlp/v1/metrics/")]
    public void Signal_suffixed_endpoint_is_rejected_for_http_protobuf(string given)
    {
        var options = new OtlpOptions { Endpoint = given, Protocol = "http/protobuf" };
        var ex = Assert.Throws<InvalidOperationException>(() => options.ResolveTraceEndpoint());
        Assert.Contains("base", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => options.ResolveMetricsEndpoint());
    }

    [Fact]
    public void Grpc_endpoints_are_untouched()
    {
        var options = new OtlpOptions { Endpoint = "http://collector:4317", Protocol = "grpc" };
        Assert.Equal(new Uri("http://collector:4317"), options.ResolveTraceEndpoint());
        Assert.Equal(new Uri("http://collector:4317"), options.ResolveMetricsEndpoint());
    }

    [Theory]
    [InlineData(" grpc ", OtlpExportProtocol.Grpc)]
    [InlineData("GRPC", OtlpExportProtocol.Grpc)]
    [InlineData("HTTP/Protobuf", OtlpExportProtocol.HttpProtobuf)]
    [InlineData(null, OtlpExportProtocol.Grpc)]
    public void Protocol_parsing_is_trimmed_and_case_insensitive(string? given, OtlpExportProtocol expected) =>
        Assert.Equal(expected, new OtlpOptions { Protocol = given }.ParseProtocol());

    // #316 — an https endpoint always resolves, in Production or anywhere else.
    [Fact]
    public void Https_endpoint_resolves_in_Production()
    {
        var options = new OtlpOptions { Endpoint = "https://collector.test:4318", Protocol = "grpc" };
        Assert.Equal(new Uri("https://collector.test:4318"), options.ResolveTraceEndpoint(isProduction: true));
        Assert.Equal(new Uri("https://collector.test:4318"), options.ResolveMetricsEndpoint(isProduction: true));
    }

    // Plaintext HTTP to a real (non-loopback) collector is fine outside
    // Production — the pre-#316 behavior every other test in this class
    // relies on — but must fail once isProduction flips true.
    [Fact]
    public void Plaintext_remote_endpoint_resolves_outside_Production()
    {
        var options = new OtlpOptions { Endpoint = "http://collector.test:4318", Protocol = "grpc" };
        Assert.Equal(new Uri("http://collector.test:4318"), options.ResolveTraceEndpoint(isProduction: false));
    }

    [Fact]
    public void Plaintext_remote_endpoint_fails_in_Production()
    {
        var options = new OtlpOptions { Endpoint = "http://collector.test:4318", Protocol = "grpc" };
        var ex = Assert.Throws<InvalidOperationException>(() => options.ResolveTraceEndpoint(isProduction: true));
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Otlp:AllowInsecureEndpoint", ex.Message);
    }

    // The documented development escape hatch: a loopback collector may stay
    // plaintext even in Production, but ONLY with the flag explicitly set.
    [Theory]
    [InlineData("http://127.0.0.1:4318")]
    [InlineData("http://localhost:4318")]
    [InlineData("http://[::1]:4318")]
    public void Plaintext_loopback_endpoint_fails_in_Production_without_the_opt_out(string endpoint)
    {
        var options = new OtlpOptions { Endpoint = endpoint, Protocol = "grpc" };
        var ex = Assert.Throws<InvalidOperationException>(() => options.ResolveTraceEndpoint(isProduction: true));
        Assert.Contains("Otlp:AllowInsecureEndpoint", ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318")]
    [InlineData("http://localhost:4318")]
    [InlineData("http://[::1]:4318")]
    public void Plaintext_loopback_endpoint_resolves_in_Production_with_the_opt_out(string endpoint)
    {
        var options = new OtlpOptions { Endpoint = endpoint, Protocol = "grpc", AllowInsecureEndpoint = true };
        var resolved = options.ResolveTraceEndpoint(isProduction: true);
        Assert.Equal(new Uri(endpoint), resolved);
    }

    // #316 review — the opt-out is NOT loopback-scoped. The sim harness (#243)
    // runs its serving app in Production against `http://otel-collector:4317`,
    // a sidecar reached by compose-service name on the stack's own private
    // network — not loopback, but equally traffic that never leaves the stack.
    // A loopback-only escape hatch failed that boot outright. This pins the
    // exact shape the sim uses so the regression can't return silently.
    [Fact]
    public void Plaintext_private_network_sidecar_resolves_in_Production_with_the_opt_out()
    {
        var options = new OtlpOptions
        {
            Endpoint = "http://otel-collector:4317",
            Protocol = "grpc",
            AllowInsecureEndpoint = true
        };

        var resolved = options.ResolveTraceEndpoint(isProduction: true);

        Assert.Equal(new Uri("http://otel-collector:4317"), resolved);
    }

    // The opt-out is the ONLY thing that permits it: unset, the same sidecar
    // endpoint still fails closed in Production.
    [Fact]
    public void Plaintext_private_network_sidecar_still_fails_without_the_opt_out()
    {
        var options = new OtlpOptions { Endpoint = "http://otel-collector:4317", Protocol = "grpc" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.ResolveTraceEndpoint(isProduction: true));

        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // #316 — userinfo/query/fragment are rejected regardless of environment:
    // they can carry a vendor credential or tenant identifier into logs.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Endpoint_with_userinfo_fails_in_every_environment(bool isProduction)
    {
        var fakeSecret = $"vendor-secret-{Guid.NewGuid():N}";
        var options = new OtlpOptions { Endpoint = $"https://user:{fakeSecret}@collector.test:4318" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.ResolveTraceEndpoint(isProduction));

        Assert.Contains("userinfo", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The message must never echo the credential it just rejected.
        Assert.DoesNotContain(fakeSecret, ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Endpoint_with_query_string_fails_in_every_environment(bool isProduction)
    {
        var fakeTenant = $"tenant-{Guid.NewGuid():N}";
        var options = new OtlpOptions { Endpoint = $"https://collector.test:4318/base?tenant={fakeTenant}" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.ResolveTraceEndpoint(isProduction));

        Assert.Contains("query", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fakeTenant, ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Endpoint_with_fragment_fails_in_every_environment(bool isProduction)
    {
        var options = new OtlpOptions { Endpoint = "https://collector.test:4318/base#section" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.ResolveTraceEndpoint(isProduction));

        Assert.Contains("fragment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Endpoint_shape_rejection_message_never_echoes_the_raw_endpoint()
    {
        var fakeSecret = $"vendor-secret-{Guid.NewGuid():N}";
        var withUserInfo = new OtlpOptions { Endpoint = $"https://svc:{fakeSecret}@collector.test:4318" };
        var withQuery = new OtlpOptions { Endpoint = $"https://collector.test:4318?key={fakeSecret}" };

        var userInfoEx = Assert.Throws<InvalidOperationException>(() => withUserInfo.ResolveTraceEndpoint());
        var queryEx = Assert.Throws<InvalidOperationException>(() => withQuery.ResolveTraceEndpoint());

        Assert.DoesNotContain(withUserInfo.Endpoint, userInfoEx.Message);
        Assert.DoesNotContain(withQuery.Endpoint, queryEx.Message);
    }
}
