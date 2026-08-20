namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Testcontainers.PostgreSql;

// The OTel SDK reads standard OTLP variables when its exporter options object is
// constructed. These cases run each exporter in a real child process so an
// ambient process variable cannot leak between xUnit tests or factories.
public sealed class OtlpSubprocessDatabaseFixture : IAsyncLifetime
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage).Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var migration = OtlpSubprocessExporterTests.StartServing(ConnectionString, psi =>
        {
            psi.ArgumentList.Add("migrate");
            psi.Environment["Otlp__Endpoint"] = "";
        });
        var (exitCode, stdout, stderr) = await migration.WaitForExitAsync(TimeSpan.FromSeconds(120));
        Assert.True(exitCode == 0, $"schema migration failed: exit={exitCode} stdout={stdout} stderr={stderr}");
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

public sealed class OtlpSubprocessExporterTests(OtlpSubprocessDatabaseFixture database)
    : IClassFixture<OtlpSubprocessDatabaseFixture>
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(20);
    private readonly OtlpSubprocessDatabaseFixture _database = database;

    [Fact]
    public async Task Standard_profile_exports_to_the_standard_endpoint_with_its_header()
    {
        using var collector = new FakeOtlpCollector();
        var headerValue = $"standard-{Guid.NewGuid():N}";
        await using var child = StartServing(psi =>
        {
            psi.Environment[OtlpConfigurationResolver.StandardEndpointKey] = collector.Endpoint;
            psi.Environment[OtlpConfigurationResolver.StandardProtocolKey] = "http/protobuf";
            psi.Environment[OtlpConfigurationResolver.StandardHeadersKey] = $"x-otlp-api-key={headerValue}";
        });

        await child.WaitUntilReadyAsync(ReadyTimeout);
        var traceId = await DriveUniqueRequestAsync(child.BaseUrl);
        var trace = await collector.WaitForRequestAsync(
            "/v1/traces", request => OtlpPayloadAssertions.IsExpectedTracePayload(request.Body, traceId), ExportTimeout);
        var metrics = await collector.WaitForRequestAsync(
            "/v1/metrics", request => OtlpPayloadAssertions.HasExpectedMetricPayload(request.Body), ExportTimeout);

        AssertHeader(trace, "x-otlp-api-key", headerValue);
        AssertHeader(metrics, "x-otlp-api-key", headerValue);
        OtlpPayloadAssertions.AssertTracePayload(trace.Body, traceId);
        OtlpPayloadAssertions.AssertMetricPayload(metrics.Body);
    }

    [Fact]
    public async Task Canonical_endpoint_never_receives_the_ambient_standard_header()
    {
        using var canonicalCollector = new FakeOtlpCollector();
        using var ambientCollector = new FakeOtlpCollector();
        var ambientHeaderValue = $"ambient-{Guid.NewGuid():N}";
        await using var child = StartServing(psi =>
        {
            psi.Environment["Otlp__Endpoint"] = canonicalCollector.Endpoint;
            psi.Environment["Otlp__Protocol"] = "http/protobuf";
            psi.Environment[OtlpConfigurationResolver.StandardEndpointKey] = ambientCollector.Endpoint;
            psi.Environment[OtlpConfigurationResolver.StandardProtocolKey] = "http/protobuf";
            psi.Environment[OtlpConfigurationResolver.StandardHeadersKey] = $"x-otlp-api-key={ambientHeaderValue}";
        });

        await child.WaitUntilReadyAsync(ReadyTimeout);
        var traceId = await DriveUniqueRequestAsync(child.BaseUrl);
        var trace = await canonicalCollector.WaitForRequestAsync(
            "/v1/traces", request => OtlpPayloadAssertions.IsExpectedTracePayload(request.Body, traceId), ExportTimeout);
        var metrics = await canonicalCollector.WaitForRequestAsync(
            "/v1/metrics", request => OtlpPayloadAssertions.HasExpectedMetricPayload(request.Body), ExportTimeout);

        Assert.DoesNotContain("x-otlp-api-key", trace.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-otlp-api-key", metrics.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        OtlpPayloadAssertions.AssertTracePayload(trace.Body, traceId);
        OtlpPayloadAssertions.AssertMetricPayload(metrics.Body);
        await ambientCollector.AssertNoRequestAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Canonical_profile_ignores_malformed_ambient_standard_transport()
    {
        using var collector = new FakeOtlpCollector();
        await using var child = StartServing(psi =>
        {
            psi.Environment["Otlp__Endpoint"] = collector.Endpoint;
            psi.Environment["Otlp__Protocol"] = "http/protobuf";
            psi.Environment[OtlpConfigurationResolver.StandardEndpointKey] = "not a uri";
            psi.Environment[OtlpConfigurationResolver.StandardProtocolKey] = "not-a-protocol";
        });

        await child.WaitUntilReadyAsync(ReadyTimeout);
        var traceId = await DriveUniqueRequestAsync(child.BaseUrl);
        var trace = await collector.WaitForRequestAsync(
            "/v1/traces", request => OtlpPayloadAssertions.IsExpectedTracePayload(request.Body, traceId), ExportTimeout);
        var metrics = await collector.WaitForRequestAsync(
            "/v1/metrics", request => OtlpPayloadAssertions.HasExpectedMetricPayload(request.Body), ExportTimeout);

        Assert.DoesNotContain("x-otlp-api-key", trace.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-otlp-api-key", metrics.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        OtlpPayloadAssertions.AssertTracePayload(trace.Body, traceId);
        OtlpPayloadAssertions.AssertMetricPayload(metrics.Body);
    }

    [Fact]
    public async Task Blank_canonical_endpoint_disables_an_ambient_standard_exporter()
    {
        using var collector = new FakeOtlpCollector();
        await using var child = StartServing(psi =>
        {
            psi.Environment["Otlp__Endpoint"] = "";
            psi.Environment[OtlpConfigurationResolver.StandardEndpointKey] = collector.Endpoint;
            psi.Environment[OtlpConfigurationResolver.StandardProtocolKey] = "http/protobuf";
        });

        await child.WaitUntilReadyAsync(ReadyTimeout);
        await DriveUniqueRequestAsync(child.BaseUrl);
        await collector.AssertNoRequestAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Production_https_endpoint_boots()
    {
        await using var child = StartServing(psi =>
        {
            ConfigureProduction(psi);
            psi.Environment["Otlp__Endpoint"] = "https://otlp.example:4318";
            psi.Environment["Otlp__Protocol"] = "grpc";
        });

        await child.WaitUntilReadyAsync(ReadyTimeout);
    }

    [Fact]
    public async Task Production_plaintext_loopback_endpoint_with_acknowledgement_boots()
    {
        using var collector = new FakeOtlpCollector();
        await using var child = StartServing(psi =>
        {
            ConfigureProduction(psi);
            psi.Environment["Otlp__Endpoint"] = collector.Endpoint;
            psi.Environment["Otlp__Protocol"] = "http/protobuf";
            psi.Environment["Otlp__AllowInsecureEndpoint"] = "true";
        });

        await child.WaitUntilReadyAsync(ReadyTimeout);
    }

    [Fact]
    public async Task Production_plaintext_private_sidecar_endpoint_with_acknowledgement_boots()
    {
        await using var child = StartServing(psi =>
        {
            ConfigureProduction(psi);
            psi.Environment["Otlp__Endpoint"] = "http://otel-collector:4317";
            psi.Environment["Otlp__Protocol"] = "grpc";
            psi.Environment["Otlp__AllowInsecureEndpoint"] = "true";
        });

        await child.WaitUntilReadyAsync(ReadyTimeout);
    }

    [Fact]
    public async Task Collector_credentials_never_appear_in_child_output()
    {
        var secret = $"collector-secret-{Guid.NewGuid():N}";
        using var standardCollector = new FakeOtlpCollector();
        await using var standard = StartServing(psi =>
        {
            psi.Environment[OtlpConfigurationResolver.StandardEndpointKey] = standardCollector.Endpoint;
            psi.Environment[OtlpConfigurationResolver.StandardProtocolKey] = "http/protobuf";
            psi.Environment[OtlpConfigurationResolver.StandardHeadersKey] = $"x-otlp-api-key={secret}";
        });
        await standard.WaitUntilReadyAsync(ReadyTimeout);
        var standardTraceId = await DriveUniqueRequestAsync(standard.BaseUrl);
        var standardTrace = await standardCollector.WaitForRequestAsync(
            "/v1/traces", request => OtlpPayloadAssertions.IsExpectedTracePayload(request.Body, standardTraceId), ExportTimeout);
        var standardMetrics = await standardCollector.WaitForRequestAsync(
            "/v1/metrics", request => OtlpPayloadAssertions.HasExpectedMetricPayload(request.Body), ExportTimeout);
        AssertHeader(standardTrace, "x-otlp-api-key", secret);
        AssertHeader(standardMetrics, "x-otlp-api-key", secret);
        OtlpPayloadAssertions.AssertTracePayload(standardTrace.Body, standardTraceId);
        OtlpPayloadAssertions.AssertMetricPayload(standardMetrics.Body);
        var (_, standardStdout, standardStderr) = await standard.StopAsync();

        using var canonicalCollector = new FakeOtlpCollector();
        await using var canonical = StartServing(psi =>
        {
            psi.Environment["Otlp__Endpoint"] = canonicalCollector.Endpoint;
            psi.Environment["Otlp__Protocol"] = "http/protobuf";
            psi.Environment["Otlp__Headers"] = $"x-otlp-api-key={secret}";
        });
        await canonical.WaitUntilReadyAsync(ReadyTimeout);
        var canonicalTraceId = await DriveUniqueRequestAsync(canonical.BaseUrl);
        var canonicalTrace = await canonicalCollector.WaitForRequestAsync(
            "/v1/traces", request => OtlpPayloadAssertions.IsExpectedTracePayload(request.Body, canonicalTraceId), ExportTimeout);
        var canonicalMetrics = await canonicalCollector.WaitForRequestAsync(
            "/v1/metrics", request => OtlpPayloadAssertions.HasExpectedMetricPayload(request.Body), ExportTimeout);
        AssertHeader(canonicalTrace, "x-otlp-api-key", secret);
        AssertHeader(canonicalMetrics, "x-otlp-api-key", secret);
        OtlpPayloadAssertions.AssertTracePayload(canonicalTrace.Body, canonicalTraceId);
        OtlpPayloadAssertions.AssertMetricPayload(canonicalMetrics.Body);
        var (_, canonicalStdout, canonicalStderr) = await canonical.StopAsync();

        foreach (var output in new[] { standardStdout, standardStderr, canonicalStdout, canonicalStderr })
        {
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
            Assert.DoesNotContain("x-otlp-api-key", output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Enabled_boot_log_reports_a_sanitized_endpoint()
    {
        await using var child = StartServing(psi =>
        {
            psi.Environment["Otlp__Endpoint"] = "https://otlp.example:4318";
            psi.Environment["Otlp__Protocol"] = "http/protobuf";
        });
        await child.WaitUntilReadyAsync(ReadyTimeout);
        var (_, stdout, stderr) = await child.StopAsync();
        var output = stdout + stderr;

        Assert.Contains("https://otlp.example:4318/v1/traces", output, StringComparison.Ordinal);
        Assert.DoesNotContain("@", output, StringComparison.Ordinal);
        Assert.DoesNotContain("?", output, StringComparison.Ordinal);
        Assert.DoesNotContain("#", output, StringComparison.Ordinal);
    }

    private static void ConfigureProduction(ProcessStartInfo psi)
    {
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["RateLimiting__TrustedProxies__0"] = "10.0.0.0/8";
        psi.Environment["AllowedHosts"] = "cluckwork-test.example";
    }

    private ServingSubprocess StartServing(Action<ProcessStartInfo> configure) =>
        StartServing(_database.ConnectionString, configure);

    internal static ServingSubprocess StartServing(
        string connectionString, Action<ProcessStartInfo> configure)
    {
        var port = GetFreeTcpPort();
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ApiDllPath);

        // Construct the child environment from an explicit safe OS allow-list,
        // then scrub OTLP transport names again before the case applies its own
        // profile. This is intentionally not xUnit-process environment mutation.
        psi.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "DOTNET_ROOT", "TMPDIR", "LANG", "LC_ALL", "USER" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                psi.Environment[name] = value;
        ScrubOtlpTransport(psi);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["ConnectionStrings__Default"] = connectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Database__MigrateOnStartup"] = "false";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        psi.Environment["RateLimiting__Login__PermitLimit"] = "1000000";
        psi.Environment["RateLimiting__Refresh__PermitLimit"] = "1000000";
        // Let the real SDK's periodic processors export within the focused-test
        // bound; these change only test scheduling, never signal destinations.
        psi.Environment["OTEL_BSP_SCHEDULE_DELAY"] = "1000";
        psi.Environment["OTEL_METRIC_EXPORT_INTERVAL"] = "1000";
        configure(psi);
        return new ServingSubprocess(Process.Start(psi)!, new Uri($"http://127.0.0.1:{port}"));
    }

    private static void ScrubOtlpTransport(ProcessStartInfo psi)
    {
        foreach (var key in psi.Environment.Keys
                     .Where(key => key.StartsWith("Otlp__", StringComparison.OrdinalIgnoreCase)
                         || key.Equals(OtlpConfigurationResolver.StandardEndpointKey, StringComparison.OrdinalIgnoreCase)
                         || key.Equals(OtlpConfigurationResolver.StandardProtocolKey, StringComparison.OrdinalIgnoreCase)
                         || key.Equals(OtlpConfigurationResolver.StandardHeadersKey, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            psi.Environment.Remove(key);
    }

    private static async Task<string> DriveUniqueRequestAsync(Uri baseUrl)
    {
        var traceId = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        using var client = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@test.local", password = "wrong-password-123!" }),
        };
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-0123456789abcdef-01");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        return traceId;
    }

    private static int GetFreeTcpPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static void AssertHeader(CapturedOtlpRequest request, string name, string expectedValue)
    {
        Assert.True(request.Headers.TryGetValue(name, out var value));
        Assert.Equal(expectedValue, value);
    }

    internal sealed class ServingSubprocess(Process process, Uri baseUrl) : IAsyncDisposable
    {
        private readonly Process _process = process;
        private readonly Task<string> _stdout = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> _stderr = process.StandardError.ReadToEndAsync();

        public Uri BaseUrl { get; } = baseUrl;

        public async Task WaitUntilReadyAsync(TimeSpan timeout)
        {
            using var client = new HttpClient { BaseAddress = BaseUrl, Timeout = TimeSpan.FromSeconds(5) };
            var deadline = DateTime.UtcNow + timeout;
            Exception? lastError = null;
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    var (_, stdout, stderr) = await WaitForExitAsync(TimeSpan.Zero);
                    throw new InvalidOperationException($"child exited before readiness. stdout={stdout} stderr={stderr}");
                }
                try
                {
                    if ((await client.GetAsync("/health/ready")).IsSuccessStatusCode) return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
            throw new TimeoutException($"child at {BaseUrl} did not become ready within {timeout}: {lastError?.Message}");
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> WaitForExitAsync(TimeSpan timeout)
        {
            if (!_process.HasExited && timeout > TimeSpan.Zero)
            {
                var waitForExit = _process.WaitForExitAsync();
                var exited = await Task.WhenAny(waitForExit, Task.Delay(timeout));
                if (exited != waitForExit)
                    throw new TimeoutException($"child did not exit within {timeout}");
            }
            return (_process.HasExited ? _process.ExitCode : -1, await _stdout, await _stderr);
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> StopAsync()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch { /* exited while stopping */ }
            await _process.WaitForExitAsync();
            return (_process.ExitCode, await _stdout, await _stderr);
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); } catch { /* process already disposed */ }
            _process.Dispose();
        }
    }
}
