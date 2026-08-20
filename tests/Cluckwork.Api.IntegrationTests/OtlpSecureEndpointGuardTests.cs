namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

// This factory deliberately remains on the canonical disabled profile inherited
// from CluckworkWebApplicationFactory. Every case below fails during telemetry
// registration, before an enabled exporter host can be created. Enabled boot and
// outbound cases live in OtlpSubprocessExporterTests so the SDK's environment
// parsing is isolated from the xUnit process.
public sealed class OtlpProductionFailureFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

public sealed class OtlpSecureEndpointGuardTests : IClassFixture<OtlpProductionFailureFactory>
{
    private readonly OtlpProductionFailureFactory _factory;

    public OtlpSecureEndpointGuardTests(OtlpProductionFailureFactory factory) => _factory = factory;

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            parts.Add(current.Message);
        return string.Join(" | ", parts);
    }

    [Fact]
    public void Production_plaintext_remote_endpoint_fails_boot_before_an_exporter_host_starts()
    {
        using var badHost = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Endpoint", "http://otlp.example:4318"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        var message = Flatten(boot!);
        Assert.Contains("https", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Otlp:AllowInsecureEndpoint", message);
    }

    [Fact]
    public void Production_plaintext_loopback_endpoint_without_opt_out_fails_boot_before_an_exporter_host_starts()
    {
        using var badHost = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Endpoint", "http://127.0.0.1:4317"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        Assert.Contains("Otlp:AllowInsecureEndpoint", Flatten(boot!));
    }

    [Fact]
    public void Production_plaintext_private_sidecar_without_opt_out_fails_boot_before_an_exporter_host_starts()
    {
        using var badHost = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Endpoint", "http://otel-collector:4317"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        Assert.Contains("https", Flatten(boot!), StringComparison.OrdinalIgnoreCase);
    }
}
