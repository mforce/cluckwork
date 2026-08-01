namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

// #316 — proves the Production https-or-loopback-opt-out guard on Otlp:Endpoint
// is actually WIRED into startup (Program.cs -> AddCluckworkTelemetry ->
// OtlpOptions.Resolve*Endpoint(isProduction)), mirroring how
// ConnectionTlsFloorWiringTests locks the #262 Postgres TLS-floor wiring and
// AllowedHostsGuardTests locks #319. The guard's LOGIC is unit-tested directly
// against OtlpOptions in OtlpExporterTests.cs; this locks the end-to-end wiring
// so a mis-wired or deleted Program.cs call leaves the unit tests green but
// fails these boot tests.
//
// Own factory (not the shared "Testing" OtlpFactory): Production config
// differs from every other test's environment, and the base
// CluckworkWebApplicationFactory never sets Otlp:Endpoint, so this guard is a
// no-op for every other Production-derived factory in the suite.
public sealed class OtlpProductionFactory : CluckworkWebApplicationFactory
{
    public const string HttpsEndpoint = "https://otlp.example:4318";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.UseSetting("Otlp:Endpoint", HttpsEndpoint);
        builder.UseSetting("Otlp:Protocol", "grpc");
    }
}

public sealed class OtlpSecureEndpointGuardTests : IClassFixture<OtlpProductionFactory>
{
    private readonly OtlpProductionFactory _factory;

    public OtlpSecureEndpointGuardTests(OtlpProductionFactory factory) => _factory = factory;

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    [Fact]
    public void Production_HttpsEndpoint_Boots()
    {
        var boot = Record.Exception(() => _factory.CreateClient());

        Assert.Null(boot);
    }

    [Fact]
    public void Production_PlaintextRemoteEndpoint_FailsBoot()
    {
        using var badHost = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Otlp:Endpoint", "http://otlp.example:4318"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        var message = Flatten(boot!);
        Assert.Contains("https", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Otlp:AllowInsecureLoopback", message);
    }

    [Fact]
    public void Production_PlaintextLoopbackEndpoint_WithoutOptOut_FailsBoot()
    {
        using var badHost = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Otlp:Endpoint", "http://127.0.0.1:4317"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        Assert.Contains("Otlp:AllowInsecureLoopback", Flatten(boot!));
    }

    [Fact]
    public void Production_PlaintextLoopbackEndpoint_WithOptOut_Boots()
    {
        using var goodHost = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Otlp:Endpoint", "http://127.0.0.1:4317");
            b.UseSetting("Otlp:AllowInsecureLoopback", "true");
        });

        var boot = Record.Exception(() => goodHost.CreateClient());

        Assert.Null(boot);
    }

    // "Impossible to hit by accident": the loopback opt-out must not also
    // wave through a real remote endpoint gone plaintext.
    [Fact]
    public void Production_PlaintextRemoteEndpoint_WithOptOutSet_StillFailsBoot()
    {
        using var badHost = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Otlp:Endpoint", "http://otlp.example:4318");
            b.UseSetting("Otlp:AllowInsecureLoopback", "true");
        });

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        Assert.Contains("https", Flatten(boot!), StringComparison.OrdinalIgnoreCase);
    }
}

// #316 — vendor auth belongs only in Otlp:Headers and must never surface in a
// log line. Own factory (Testing env is enough; the redaction contract isn't
// Production-specific) with a DI-tapped sink, mirroring
// RequestLoggingTests.CollectingSink.
public sealed class OtlpHeadersLoggingFactory : CluckworkWebApplicationFactory
{
    public CollectingSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Otlp:Endpoint", "https://otlp.example:4318");
        builder.UseSetting("Otlp:Protocol", "http/protobuf");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogEventSink>(Sink));
    }

    public sealed class CollectingSink : ILogEventSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

public sealed class OtlpHeadersLoggingTests : IClassFixture<OtlpHeadersLoggingFactory>
{
    private readonly OtlpHeadersLoggingFactory _factory;

    public OtlpHeadersLoggingTests(OtlpHeadersLoggingFactory factory) => _factory = factory;

    [Fact]
    public void Boot_logs_never_contain_the_configured_Otlp_Headers_credential()
    {
        var fakeToken = $"otlp-vendor-token-{Guid.NewGuid():N}";
        using var host = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Otlp:Headers", $"Authorization=Bearer {fakeToken}"));

        host.CreateClient();

        Assert.DoesNotContain(_factory.Sink.Events, e =>
            e.RenderMessage().Contains(fakeToken)
            || e.Properties.Values.Any(v => v.ToString().Contains(fakeToken)));
    }

    // The enabled-export boot line must carry only scheme/host/port/path. The
    // sink is shared across this fixture's tests (one host boot per test, all
    // captured in the same queue), so assert against the boot THIS test
    // triggers rather than assuming it is the only "OTLP export enabled" line.
    [Fact]
    public void Boot_log_reports_a_sanitized_endpoint()
    {
        using var host = _factory.WithWebHostBuilder(_ => { });

        host.CreateClient();

        var enabledLines = _factory.Sink.Events
            .Where(e => e.RenderMessage().StartsWith("OTLP export enabled", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(enabledLines);
        Assert.All(enabledLines, e =>
        {
            var rendered = e.RenderMessage();
            Assert.Contains("https://otlp.example:4318/v1/traces", rendered);
            Assert.DoesNotContain("@", rendered);
            Assert.DoesNotContain("?", rendered);
            Assert.DoesNotContain("#", rendered);
        });
    }
}
