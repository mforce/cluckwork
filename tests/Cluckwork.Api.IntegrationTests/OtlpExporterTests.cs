namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Configuration;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenTelemetry.Exporter;

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

    // "localhost:4317" parses as an ABSOLUTE uri with Scheme=localhost — other
    // OTel SDKs accept the shape, ours must reject it at boot rather than let
    // the exporter fail later with an unpointed error (agent review of #226).
    [Fact]
    public void Non_http_scheme_endpoint_fails_at_startup_with_a_pointed_message()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otlp:Endpoint", "localhost:4317"));

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

    // Eager validation must not hide behind the endpoint gate: a typo'd
    // protocol with export disabled still fails at boot (codex review of #226).
    [Fact]
    public void Unknown_otlp_protocol_fails_at_startup_even_without_an_endpoint()
    {
        using var broken = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otlp:Endpoint", "");
            builder.UseSetting("Otlp:Protocol", "carrier-pigeon");
        });

        var ex = Record.Exception(() => broken.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Otlp:Protocol", ex!.Message);
    }

    // The real thing: spans must LEAVE the process carrying our service name.
    // A local HTTP listener plays OTLP collector; disposing the host force-
    // flushes the batch exporter, so no dependence on the batch schedule. The
    // driver request is an API route — /health/* is span-filtered by design.
    [Fact]
    public async Task Spans_are_posted_to_the_configured_otlp_endpoint()
    {
        using var collector = new FakeOtlpCollector();
        var exporting = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otlp:Endpoint", collector.Endpoint);
            builder.UseSetting("Otlp:Protocol", "http/protobuf");
        });
        try
        {
            var response = await exporting.CreateClient().GetAsync("/api/v1/flocks");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            exporting.Dispose();
        }

        var (path, body) = await collector.WaitForRequestAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("/v1/traces", path);
        // Protobuf embeds resource strings verbatim — the service name proves
        // this is a real span payload, not an empty keep-alive.
        Assert.Contains("Cluckwork.Api", System.Text.Encoding.ASCII.GetString(body));
    }
}

// Pure endpoint-resolution rules (unit-level, no host): the exporter uses an
// explicit Endpoint AS-IS, so the app owns the /v1/traces append for
// http/protobuf — including the edges reviewers flagged (trailing slashes,
// query strings, vendor base paths).
public sealed class OtlpEndpointResolutionTests
{
    private static Uri Resolve(string endpoint, string? protocol = "http/protobuf") =>
        new OtlpOptions { Endpoint = endpoint, Protocol = protocol }.ResolveTraceEndpoint();

    [Theory]
    [InlineData("http://collector:4318", "http://collector:4318/v1/traces")]
    [InlineData("http://collector:4318/", "http://collector:4318/v1/traces")]
    [InlineData("https://host/otlp", "https://host/otlp/v1/traces")]
    [InlineData("http://collector:4318/v1/traces", "http://collector:4318/v1/traces")]
    [InlineData("http://collector:4318/v1/traces/", "http://collector:4318/v1/traces/")]
    public void Http_protobuf_appends_the_signal_path_exactly_once(string given, string expected) =>
        Assert.Equal(new Uri(expected), Resolve(given));

    [Fact]
    public void Query_string_survives_the_append() =>
        Assert.Equal(new Uri("http://collector:4318/base/v1/traces?tenant=1"),
            Resolve("http://collector:4318/base?tenant=1"));

    [Fact]
    public void Grpc_endpoint_is_untouched() =>
        Assert.Equal(new Uri("http://collector:4317"), Resolve("http://collector:4317", "grpc"));

    [Theory]
    [InlineData(" grpc ", OtlpExportProtocol.Grpc)]
    [InlineData("GRPC", OtlpExportProtocol.Grpc)]
    [InlineData("HTTP/Protobuf", OtlpExportProtocol.HttpProtobuf)]
    [InlineData(null, OtlpExportProtocol.Grpc)]
    public void Protocol_parsing_is_trimmed_and_case_insensitive(string? given, OtlpExportProtocol expected) =>
        Assert.Equal(expected, new OtlpOptions { Protocol = given }.ParseProtocol());
}

[CollectionDefinition(Name)]
public sealed class OtlpCollection : ICollectionFixture<OtlpFactory>
{
    public const string Name = "otlp";
}

// Minimal OTLP "collector": captures the first request's path + body. Listener
// failures surface through the TaskCompletionSource instead of degrading into
// a generic timeout (cavecrew review of #226); Start retries a fresh port to
// close the probe-then-bind race.
internal sealed class FakeOtlpCollector : IDisposable
{
    private readonly System.Net.HttpListener _listener = new();
    private readonly TaskCompletionSource<(string Path, byte[] Body)> _firstRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeOtlpCollector()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                break;
            }
            catch (System.Net.HttpListenerException) when (attempt < 3)
            {
                // Port grabbed between probe and bind — rare; try another.
            }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    using var buffer = new MemoryStream();
                    await ctx.Request.InputStream.CopyToAsync(buffer);
                    _firstRequest.TrySetResult((ctx.Request.Url!.AbsolutePath, buffer.ToArray()));
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException
                                       && !_listener.IsListening)
            {
                // Normal shutdown: Dispose() stopped the listener mid-accept.
            }
            catch (Exception ex)
            {
                _firstRequest.TrySetException(ex);
            }
        });
    }

    public string Endpoint { get; private set; }

    public async Task<(string Path, byte[] Body)> WaitForRequestAsync(TimeSpan timeout)
    {
        var winner = await Task.WhenAny(_firstRequest.Task, Task.Delay(timeout));
        Assert.True(winner == _firstRequest.Task, "no OTLP export arrived before the timeout");
        return await _firstRequest.Task;
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
        try { _listener.Stop(); } catch { /* not listening / already disposed */ }
        ((IDisposable)_listener).Dispose();
    }
}
