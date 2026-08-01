namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

// #307 acceptance criterion: "Two independently hosted API instances sharing
// one Postgres database receive the same authenticated write concurrently and
// produce exactly one domain mutation and one durable side effect" — and the
// protocol must be proven to fail against the OLD middleware and pass against
// the new one.
//
// This spawns TWO REAL, SEPARATE OS PROCESSES running the actual built
// Cluckwork.Api.dll (the same binary a deploy runs), sharing one Postgres
// Testcontainer. This is deliberately NOT two in-process WebApplicationFactory
// hosts: the pre-#307 middleware's coordination was a `static readonly
// SemaphoreSlim[] Stripes` field on the IdempotencyMiddleware TYPE — shared by
// every WebApplicationFactory in the SAME test process/AppDomain, which would
// still (accidentally) serialize two in-process "replicas" and mask exactly
// the bug this issue exists to fix. Two separate `dotnet` processes have two
// separate CLRs and cannot share that field — only a real replica boundary
// (or a database-coordinated protocol) can serialize them.
public sealed class MultiInstanceIdempotencyTests : IAsyncLifetime
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SubprocessExitTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();

    private readonly List<Process> _liveProcesses = [];
    private readonly List<Task> _drains = [];

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
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
        // SAME signing key material, issuer and audience on every process —
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
    // against the shared Postgres, with its own ephemeral port and its own
    // OS process. Database:MigrateOnStartup=false because MigrateSchemaAsync
    // already applied the schema — mirrors the real deploy split (#263).
    private (Process Process, string BaseUrl) StartServingInstance(string adminEmail, string adminPassword)
    {
        var port = GetFreeTcpPort();
        var psi = MakeBaseStartInfo();
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["Database__MigrateOnStartup"] = "false";
        psi.Environment["RateLimiting__Login__PermitLimit"] = "1000000";
        psi.Environment["RateLimiting__Refresh__PermitLimit"] = "1000000";
        // Real boot-time seeding (DatabaseSeeder) — not test-harness DI
        // reach-in — provisions the default account + admin user, exactly
        // how a real deploy's first boot would.
        psi.Environment["Seed__Enabled"] = "true";
        psi.Environment["Seed__AdminEmail"] = adminEmail;
        psi.Environment["Seed__AdminPassword"] = adminPassword;
        psi.Environment["Seed__AccountName"] = "Multi-Instance Race Farm";

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

    // The acceptance-criterion test: two independently hosted instances,
    // sharing one Postgres, receive the SAME authenticated write concurrently
    // and must produce exactly one domain mutation and one durable side
    // effect. Uses an expense (append-only, no natural-key uniqueness to
    // accidentally save the day) as the probe — the same reasoning
    // IdempotencyReplayTests.SameKey_ConcurrentRequests_OnlyOneSideEffect
    // documents for the in-process case.
    [Fact]
    public async Task ConcurrentSameKeyWrite_AcrossTwoReplicas_ProducesExactlyOneDomainMutation()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@multiinstance.test.local";
        var adminPassword = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

        var (_, urlA) = StartServingInstance(adminEmail, adminPassword);
        using var httpA = new HttpClient { BaseAddress = new Uri(urlA), Timeout = TimeSpan.FromSeconds(30) };
        await WaitUntilReadyAsync(httpA, ReadyTimeout);

        var (_, urlB) = StartServingInstance(adminEmail, adminPassword);
        using var httpB = new HttpClient { BaseAddress = new Uri(urlB), Timeout = TimeSpan.FromSeconds(30) };
        await WaitUntilReadyAsync(httpB, ReadyTimeout);

        // Everything from here on is real HTTP against the real, seeded
        // admin — no direct DB/DI reach-in from the test.
        var loginResponse = await httpA.PostAsJsonAsync(
            "/api/v1/auth/login", new { email = adminEmail, password = adminPassword });
        Assert.True(loginResponse.IsSuccessStatusCode,
            $"login against instance A failed: {loginResponse.StatusCode} {await loginResponse.Content.ReadAsStringAsync()}");
        var accessToken = ExtractString(await loginResponse.Content.ReadAsStringAsync(), "accessToken");

        // The SAME bearer token authenticates against BOTH instances — proof
        // they share signing key/issuer/audience like two real replicas
        // behind one load balancer would.
        httpA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        httpB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var categoryId = await CreateExpenseCategoryAsync(httpA);

        var body = new
        {
            expenseCategoryId = categoryId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            description = "Multi-instance concurrent write",
            amountMinorUnits = 12_34L,
            flockId = (Guid?)null,
            note = (string?)null
        };
        var key = Guid.NewGuid().ToString();

        // Fire the SAME Idempotency-Key write at BOTH independently hosted
        // instances concurrently.
        var taskA = PostExpenseAsync(httpA, key, body);
        var taskB = PostExpenseAsync(httpB, key, body);
        var results = await Task.WhenAll(taskA, taskB);

        foreach (var result in results)
            Assert.Equal(HttpStatusCode.Created, result.Status);
        // Criterion 2: both callers get the same status and body.
        Assert.Equal(results[0].Body, results[1].Body);

        // Criterion 1, the load-bearing count: exactly one row despite two
        // replicas both having executed the write concurrently.
        var count = await CountExpensesAsync(categoryId);
        Assert.Equal(1, count);
    }

    private static async Task<Guid> CreateExpenseCategoryAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/expense-categories");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { name = $"Cat-{Guid.NewGuid():N}"[..12] });
        var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"category create failed: {response.StatusCode} {raw}");
        return Guid.Parse(ExtractString(raw, "id"));
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostExpenseAsync(
        HttpClient client, string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/expenses");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<long> CountExpensesAsync(Guid categoryId)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT count(*) FROM "Expenses" WHERE "ExpenseCategoryId" = @categoryId""", connection);
        command.Parameters.AddWithValue("categoryId", categoryId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Deliberately NOT a typed record + ReadFromJsonAsync<T>: the API's JSON
    // responses use ASP.NET's default camelCase web options, and this test
    // project's own JsonContent.Create/ReadFromJsonAsync calls elsewhere rely
    // on the SAME implicit default — pulling a single field by name via
    // JsonDocument sidesteps any doubt about case-sensitivity of an untyped
    // deserialization target.
    private static string ExtractString(string json, string propertyName) =>
        JsonDocument.Parse(json).RootElement.GetProperty(propertyName).GetString()!;
}
