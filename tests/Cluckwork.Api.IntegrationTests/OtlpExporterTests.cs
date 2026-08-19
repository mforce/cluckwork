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

        var body = await collector.WaitForPathAsync("/v1/traces", TimeSpan.FromSeconds(15));
        // Protobuf embeds resource strings verbatim — the service name proves
        // this is a real span payload, not an empty keep-alive.
        Assert.Contains("Cluckwork.Api", System.Text.Encoding.ASCII.GetString(body));
    }

    // #215 — metrics ride the same pipeline: host dispose force-flushes the
    // periodic reader, so request/runtime/DB meters must land on /v1/metrics.
    [Fact]
    public async Task Metrics_are_posted_to_the_configured_otlp_endpoint()
    {
        using var collector = new FakeOtlpCollector();
        var exporting = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otlp:Endpoint", collector.Endpoint);
            builder.UseSetting("Otlp:Protocol", "http/protobuf");
        });
        try
        {
            // Login queries the user store — a real DB round-trip, so the
            // Npgsql/EF instruments record at least one measurement
            // (unrecorded histograms are omitted from the export entirely).
            var response = await exporting.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/login", new { farmCode = TestHarness.DefaultFarmCode, email = "nobody@test.local", password = "wrong-password-123!" });
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            exporting.Dispose();
        }

        var body = await collector.WaitForPathAsync("/v1/metrics", TimeSpan.FromSeconds(15));
        var text = System.Text.Encoding.ASCII.GetString(body);
        // Instrument names ride verbatim in the protobuf — one representative
        // per required source (#215 AC 1): request histograms, runtime, Npgsql,
        // EF Core. Names observed from a real export, not guessed.
        Assert.Contains("Cluckwork.Api", text);
        Assert.Contains("http.server.request.duration", text);
        Assert.Contains("dotnet.gc.collections", text);
        Assert.Contains("db.client.operation.duration", text);
        Assert.Contains("microsoft.entityframeworkcore", text);
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

[CollectionDefinition(Name)]
public sealed class OtlpCollection : ICollectionFixture<OtlpFactory>
{
    public const string Name = "otlp";
}

// Minimal OTLP "collector": captures each signal path's first body — traces
// and metrics arrive as separate POSTs in nondeterministic order (#215).
// Listener failures surface through the fault task instead of degrading into
// a generic timeout (cavecrew review of #226); Start retries a fresh port to
// close the probe-then-bind race.
internal sealed class FakeOtlpCollector : IDisposable
{
    private readonly System.Net.HttpListener _listener = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _byPath = new();
    private readonly TaskCompletionSource<byte[]> _fault =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<byte[]> For(string path) =>
        _byPath.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

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
                    For(ctx.Request.Url!.AbsolutePath).TrySetResult(buffer.ToArray());
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
                _fault.TrySetException(ex);
            }
        });
    }

    public string Endpoint { get; private set; }

    public async Task<byte[]> WaitForPathAsync(string path, TimeSpan timeout)
    {
        var request = For(path).Task;
        var winner = await Task.WhenAny(request, _fault.Task, Task.Delay(timeout));
        if (winner == _fault.Task) await _fault.Task; // rethrow the listener failure
        Assert.True(winner == request, $"no OTLP export arrived on {path} before the timeout");
        return await request;
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
