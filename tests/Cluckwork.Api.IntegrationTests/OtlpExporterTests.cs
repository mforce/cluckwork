namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

// #214 — the OTLP exporter is config-gated: no Otlp:Endpoint means no exporter
// (today's behavior, zero overhead), a valid endpoint boots and exports, and a
// malformed value fails at startup — not silently, not on the first request —
// matching the repo's eager-config-validation convention.
public sealed class OtlpFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Otlp:Endpoint", "http://127.0.0.1:4317");
    }
}

[Collection(OtlpCollection.Name)]
public sealed class OtlpExporterTests(OtlpFactory factory)
{
    [Fact]
    public async Task App_boots_and_serves_with_a_valid_otlp_endpoint_configured()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public void Malformed_otlp_endpoint_fails_at_startup_with_a_pointed_message()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Endpoint", "not a uri"));

        var ex = Record.Exception(() => broken.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Otlp:Endpoint", ex!.Message);
    }

    [Fact]
    public void Unknown_otlp_protocol_fails_at_startup_with_a_pointed_message()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Protocol", "carrier-pigeon"));

        var ex = Record.Exception(() => broken.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Otlp:Protocol", ex!.Message);
    }

    // The real thing: spans must LEAVE the process. A local HTTP listener plays
    // OTLP collector; disposing the host force-flushes the batch exporter, so
    // the assertion doesn't depend on the batch schedule.
    [Fact]
    public async Task Spans_are_posted_to_the_configured_otlp_endpoint()
    {
        using var collector = new FakeOtlpCollector();
        var exporting = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otlp:Endpoint", collector.Endpoint);
            builder.UseSetting("Otlp:Protocol", "http/protobuf");
        });

        var response = await exporting.CreateClient().GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
        exporting.Dispose();

        var path = await collector.WaitForRequestAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("/v1/traces", path);
    }

    private sealed class FakeOtlpCollector : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly TaskCompletionSource<string> _firstRequestPath =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeOtlpCollector()
        {
            var port = FreePort();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    _firstRequestPath.TrySetResult(ctx.Request.Url!.AbsolutePath);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            });
        }

        public string Endpoint { get; }

        public async Task<string> WaitForRequestAsync(TimeSpan timeout)
        {
            var winner = await Task.WhenAny(_firstRequestPath.Task, Task.Delay(timeout));
            Assert.True(winner == _firstRequestPath.Task, "no OTLP export arrived before the timeout");
            return await _firstRequestPath.Task;
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            ((IDisposable)_listener).Dispose();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class OtlpCollection : ICollectionFixture<OtlpFactory>
{
    public const string Name = "otlp";
}
