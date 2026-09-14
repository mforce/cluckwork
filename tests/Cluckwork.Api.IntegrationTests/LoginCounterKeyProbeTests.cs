namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Testcontainers.PostgreSql;
// StackExchange.Redis exports its own TestHarness, which collides with the
// suite's; alias the one this file needs.
using TestHarness = Cluckwork.Api.IntegrationTests.Infrastructure.TestHarness;
using StackExchange.Redis;
using Testcontainers.Redis;

// #840 — the census reproduced "expected 429, got 401" once in 36 runs and got
// 0 failures in 80 instrumented runs. It recorded what the counter was asked to
// count and never who asked, so it could not tell a foreign spender from a
// fallback. The login bucket key is the client address alone (RateLimitKey.ForClient),
// so any concurrently running class on the loopback address spends this budget.
//
// This reads the shared key directly instead of inferring it from a status code.
// The budget sharing is correct by design (#544) and the collision belongs to the
// suite, not the limiter, so the foreign spend is reported rather than asserted:
// a green run is information too.
public sealed class LoginCounterKeyProbeTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    // Above the shipped login PermitLimit (10 per 900 s), so the burst always
    // crosses the budget and the report shows where the 429s start.
    private const int PermitBurst = 14;

    // The same pinned images MultiInstanceRateLimitTests uses, verbatim.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();

    private readonly RedisContainer _redis =
        new RedisBuilder("redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2").Build();

    private IConnectionMultiplexer _mux = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        await MigrateSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _mux.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    // #263 — the migrate verb applies the schema, so the serving child never runs DDL.
    private async Task MigrateSchemaAsync()
    {
        var process = Process.Start(MakeStartInfo("migrate"))!;
        var (exitCode, stdout, stderr) = await SeedCommandRunner
            .RunToCompletionAsync(process, TimeSpan.FromSeconds(30));
        Assert.True(exitCode == 0, $"schema migration failed: exit={exitCode} stdout={stdout} stderr={stderr}");
    }

    // "{cluckwork:win:auth-login:127.0.0.1}:<bucket>" — the braces are the cluster
    // hash tag RedisFixedWindowCounter adds, and the bucket is floor(server-ms /
    // window), so a 900 s window turns over every fifteen minutes. Scanned rather
    // than read by name, so a drift in namespace, prefix, or hash-tag shape shows
    // up as "no keys" instead of a false zero.
    private async Task<Dictionary<string, long>> ReadLoginKeysAsync()
    {
        var db = _mux.GetDatabase();
        var found = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var endpoint in _mux.GetEndPoints())
        {
            var server = _mux.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: "*auth-login:127.0.0.1*"))
            {
                var value = await db.StringGetAsync(key);
                if (!value.IsNullOrEmpty)
                {
                    found[key.ToString()] = (long)value;
                }
            }
        }

        return found;
    }

    private ProcessStartInfo MakeStartInfo(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // "Testing", not "Development": a spawned process must not pick up the
        // developer's local user-secrets (same reason CluckworkWebApplicationFactory
        // pins it).
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        psi.Environment["ConnectionStrings__Default"] = _postgres.GetConnectionString();
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        // #263 — the migrate verb is the authority on schema; the serving process
        // never runs DDL.
        psi.Environment["Database__MigrateOnStartup"] = "false";
        psi.Environment["SharedState__Redis__ConnectionString"] = _redis.GetConnectionString();
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return psi;
    }

    [Fact]
    public async Task Probe_login_counter_key_across_the_loopback_bucket()
    {
        // A real serving child, started WITHOUT the readiness wait: /health/ready
        // needs the database, while the limiter runs before the endpoint
        // (RequireRateLimiting on /auth/login, UseRateLimiter ahead of routing).
        // Polling the login route observes the first increment the moment the
        // policy is live instead of after a database nobody here queries is
        // declared healthy.
        var child = ServingSubprocess.Start(MakeStartInfo(), ServingSubprocess.FreeTcpPort());
        using var http = new HttpClient
        {
            BaseAddress = child.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        };

        async Task<HttpStatusCode> PostLoginAsync()
        {
            using var response = await http.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { farmCode = TestHarness.DefaultFarmCode, email = "nobody@example.com", password = "WrongPassw0rd!" });
            return response.StatusCode;
        }

        try
        {
            // 401 means the request reached the handler, 429 means the budget
            // refused it; either way the counter is live.
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (true)
            {
                try
                {
                    if (await PostLoginAsync() is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Not listening yet, or accepted before the pipeline was ready.
                }

                Assert.True(DateTime.UtcNow < deadline, "the login policy never became live within the readiness timeout");
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            var before = await ReadLoginKeysAsync();

            var statuses = new List<HttpStatusCode> { await PostLoginAsync() };
            for (var i = 1; i < PermitBurst; i++)
            {
                statuses.Add(await PostLoginAsync());
            }

            var after = await ReadLoginKeysAsync();

            var report = new
            {
                test = nameof(Probe_login_counter_key_across_the_loopback_bucket),
                permitBurst = PermitBurst,
                observedStatuses = statuses.Select(s => (int)s).ToArray(),
                keysBefore = before,
                keysAfter = after,
                // Anything already in the bucket when the burst starts was spent by
                // another class on this loopback address.
                spendBeforeThisProbe = before.Sum(kv => kv.Value),
                // A bucket rollover mid-burst would account for a delta larger than
                // the burst without any foreign writer, so the two are reported
                // apart rather than summed.
                bucketCountAfter = after.Count,
                spendOnKeysThisProbeDidNotCreate =
                    after.Where(kv => !before.ContainsKey(kv.Key)).Sum(kv => kv.Value),
            };

            Console.WriteLine("CLUCKWORK_840_PROBE " + JsonSerializer.Serialize(report));

            // The counter is what is under test, so it is what is asserted: every
            // request through the login policy increments the loopback key, including
            // the ones the budget refused.
            Assert.Equal(PermitBurst, statuses.Count);
            Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
            Assert.True(after.Sum(kv => kv.Value) - before.Sum(kv => kv.Value) >= PermitBurst,
                $"the login policy did not increment the loopback counter {PermitBurst} times");
        }
        finally
        {
            await child.DisposeAsync();
        }
    }
}
