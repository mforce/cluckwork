namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

// #544 acceptance proof: the IP-keyed auth rate limiters now enforce ONE
// COMBINED per-IP budget across replicas via the shared IFixedWindowCounter
// (#543), not one per-process budget. The pre-#544 in-process
// GetFixedWindowLimiter state lived in each process, so N replicas allowed ~N×
// the intended budget split across them.
//
// This spawns TWO REAL, SEPARATE OS PROCESSES running the actual built
// Cluckwork.Api.dll (harness shape copied from MultiInstanceIdempotencyTests),
// sharing ONE Postgres Testcontainer AND ONE Redis Testcontainer: both
// subprocesses get the same SharedState:Redis:ConnectionString, so their
// counters are the same shared Redis counter. If the budget were still
// per-process, the PermitLimit+1-th request split across the two instances
// would have been admitted (each instance under its own budget) and this test
// would fail.
public sealed class MultiInstanceRateLimitTests : IAsyncLifetime
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SubprocessExitTimeout = TimeSpan.FromSeconds(30);

    // #544 — small, fast budget: 5 logins per 900s window per IP.
    private const int PermitLimit = 5;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();

    // The SAME pinned image string SharedState/RedisFixture.cs uses, verbatim.
    private readonly RedisContainer _redis =
        new RedisBuilder("redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2").Build();

    private readonly List<Process> _liveProcesses = [];
    private readonly List<Task> _drains = [];

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _redis.StartAsync();
        await MigrateSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var process in _liveProcesses)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
            process.Dispose();
        }
        try { await Task.WhenAll(_drains); } catch { /* best-effort drain */ }
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private ProcessStartInfo MakeBaseStartInfo(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ApiDllPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // "Testing" (not "Development"): must not load the developer's local
        // user-secrets into a spawned process (CluckworkWebApplicationFactory
        // uses the same environment for the same reason).
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        psi.Environment["ConnectionStrings__Default"] = _postgres.GetConnectionString();
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        // #544 — BOTH processes share ONE Redis, so the auth-rate-limit
        // counter is one shared counter, not two per-process ones.
        psi.Environment["SharedState__Redis__ConnectionString"] = _redis.GetConnectionString();
        // Same signing key material, issuer and audience on every process —
        // TestJwtKeys is one Lazy<T> per TEST process, so a token minted by
        // instance A is verifiable by instance B even though they are
        // separate OS processes with no shared memory.
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return psi;
    }

    // #263 — the migrate verb is the real pre-deploy-job entrypoint; applying
    // the schema this way (rather than reaching into AppDbContext from the
    // test) exercises the exact path a real multi-instance deploy uses.
    private async Task MigrateSchemaAsync()
    {
        var psi = MakeBaseStartInfo("migrate");
        var process = Process.Start(psi)!;
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(process, SubprocessExitTimeout);
        Assert.True(exitCode == 0, $"schema migration failed: exit={exitCode} stdout={stdout} stderr={stderr}");
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Boots a REAL serving instance (no CLI verb — the normal Kestrel path)
    // against the shared Postgres + shared Redis, with its own ephemeral port
    // and its own OS process. Database:MigrateOnStartup=false because
    // MigrateSchemaAsync already applied the schema (#263).
    //
    // #544 — the login budget is SMALL on purpose (5 per 900s). The
    // idempotency harness sets a 1000000 override that DISABLES the limiter —
    // the opposite of what this test needs, so it deliberately does not.
    private (Process Process, string BaseUrl) StartServingInstance()
    {
        var port = GetFreeTcpPort();
        var psi = MakeBaseStartInfo();
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["Database__MigrateOnStartup"] = "false";
        psi.Environment["RateLimiting__Login__PermitLimit"] = PermitLimit.ToString();
        psi.Environment["RateLimiting__Login__WindowSeconds"] = "900";

        var process = Process.Start(psi)!;
        _liveProcesses.Add(process);
        // A long-running server's stdout/stderr must be drained for its whole
        // life — an unread pipe fills its OS buffer and the child blocks on
        // write (same hazard SeedCommandRunner documents for the one-shot
        // verbs, just for the process's entire lifetime here instead of a
        // bounded wait).
        _drains.Add(DrainAsync(process.StandardOutput));
        _drains.Add(DrainAsync(process.StandardError));
        return (process, $"http://127.0.0.1:{port}");
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } }
        catch { /* process exited / pipe closed */ }
    }

    private static async Task WaitUntilReadyAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        throw new TimeoutException(
            $"instance at {client.BaseAddress} did not become ready within {timeout}"
            + (lastError is null ? "" : $" (last error: {lastError.Message})"));
    }

    // #544 acceptance criterion: two independently hosted instances sharing
    // ONE Redis counter enforce ONE combined per-IP login budget.
    [Fact]
    public async Task Login_budget_is_shared_across_two_instances_over_one_redis()
    {
        // Bootstrap is NOT needed: an unknown-user login still passes through
        // the limiter middleware before any account lookup — exactly the
        // pre-lookup property under test. MigrateSchemaAsync already ran in
        // InitializeAsync, so /health/ready passes and the app serves.

        var (_, urlA) = StartServingInstance();
        using var httpA = new HttpClient { BaseAddress = new Uri(urlA), Timeout = TimeSpan.FromSeconds(30) };
        await WaitUntilReadyAsync(httpA, ReadyTimeout);

        var (_, urlB) = StartServingInstance();
        using var httpB = new HttpClient { BaseAddress = new Uri(urlB), Timeout = TimeSpan.FromSeconds(30) };
        await WaitUntilReadyAsync(httpB, ReadyTimeout);

        // Both clients hit 127.0.0.1, so the server sees the same loopback
        // client IP on both instances → the SAME derived key on both.
        var requests = new List<(HttpClient Client, string Instance)>
        {
            (httpA, "A"), (httpA, "A"), (httpA, "A"), (httpB, "B"), (httpB, "B"),
        };
        Assert.Equal(PermitLimit, requests.Count);

        // The first PermitLimit requests, SPLIT across the two instances
        // (3 to A, 2 to B for PermitLimit 5): none of them may be a 429.
        foreach (var (client, instance) in requests)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { email = "nobody@example.com", password = "WrongPassw0rd!" });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        // The load-bearing assertion: the PermitLimit+1-th request is refused
        // even though NEITHER instance individually served more than PermitLimit
        // requests — instance A saw 3, instance B saw 2+1 = 3, both under the
        // per-process budget. This is proof the budget is ONE shared Redis
        // counter, not two per-process counters: a per-process limiter (the
        // pre-#544 GetFixedWindowLimiter wiring) would have allowed up to
        // 2 × PermitLimit requests split this way, so this test fails against
        // the old in-process wiring.
        var overLimit = await httpB.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "nobody@example.com", password = "WrongPassw0rd!" });
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }
}
