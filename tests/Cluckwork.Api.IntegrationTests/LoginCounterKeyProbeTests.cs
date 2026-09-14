namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
// StackExchange.Redis exports its own TestHarness, which collides with the
// suite's; alias the one this file needs.
using TestHarness = Cluckwork.Api.IntegrationTests.Infrastructure.TestHarness;
using StackExchange.Redis;
using Testcontainers.Redis;

// #840 — the census reproduced "expected 429, got 401" once in 36 runs and got
// 0 failures in 80 instrumented runs: it had the reproduction and not the trace.
// Its instrumentation recorded what the counter was asked to count and never who
// asked, and the login bucket key is "auth-login:<client-ip>" alone
// (RateLimitKey.ForClient), so any concurrently running class that presents the
// loopback address spends the budget this probe measures.
//
// It reads the shared key directly instead of inferring it from a status code,
// and emits instead of asserting: the budget sharing is correct by design (#544)
// and the collision belongs to the test suite, not the limiter, so a green run
// is information too. Unattributed spend on the key answers the one question —
// was the budget wrong because something else spent it, or because the counter
// fell back?
public sealed class LoginCounterKeyProbeTests : IAsyncLifetime
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    private const int WindowSeconds = 900;
    private const int PermitLimit = 5;

    // The same pinned image MultiInstanceRateLimitTests uses, verbatim.
    private readonly RedisContainer _redis =
        new RedisBuilder("redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2").Build();

    private IConnectionMultiplexer _mux = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _mux.DisposeAsync();
        await _redis.DisposeAsync();
    }

    // "{cluckwork:auth-login:127.0.0.1}:<bucket>" — the braces are the cluster
    // hash tag RedisFixedWindowCounter adds. Scanned rather than read by name, so
    // a drift in the namespace or hash-tag shape shows up as "no keys" instead of
    // a false zero.
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

    private ProcessStartInfo MakeStartInfo()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ApiDllPath);

        // "Testing", not "Development": a spawned process must not pick up the
        // developer's local user-secrets (same reason CluckworkWebApplicationFactory
        // pins it).
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        // The login policy runs before any account lookup (#544), so no database
        // is reachable or needed — but the serving boot resolves the string.
        psi.Environment["ConnectionStrings__Default"] =
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Database__MigrateOnStartup"] = "false";
        psi.Environment["SharedState__Redis__ConnectionString"] = _redis.GetConnectionString();
        psi.Environment["RateLimiting__Login__PermitLimit"] = PermitLimit.ToString();
        psi.Environment["RateLimiting__Login__WindowSeconds"] = WindowSeconds.ToString();
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return psi;
    }

    [Fact]
    public async Task Probe_login_counter_key_across_the_loopback_bucket()
    {
        await using var instance = await ServingSubprocess.StartReadyAsync(MakeStartInfo(), ReadyTimeout);
        using var http = new HttpClient { BaseAddress = instance.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };

        var before = await ReadLoginKeysAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < PermitLimit + 2; i++)
        {
            var response = await http.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { farmCode = TestHarness.DefaultFarmCode, email = "nobody@example.com", password = "WrongPassw0rd!" });
            statuses.Add(response.StatusCode);
        }

        var after = await ReadLoginKeysAsync();

        var report = new
        {
            test = nameof(Probe_login_counter_key_across_the_loopback_bucket),
            permitLimit = PermitLimit,
            windowSeconds = WindowSeconds,
            observedStatuses = statuses.Select(s => (int)s).ToArray(),
            keysBefore = before,
            keysAfter = after,
            // Spend on a key this probe did not create names a foreign spender;
            // a full budget with a non-429 status names the fallback path
            // (ResilientFixedWindowCounter caught a Redis error and counted
            // locally instead).
            spendBeforeThisTest = before.Sum(kv => kv.Value),
            spendOnKeysThisTestDidNotCreate =
                after.Where(kv => !before.ContainsKey(kv.Key)).Sum(kv => kv.Value),
        };

        Console.WriteLine("CLUCKWORK_840_PROBE " + JsonSerializer.Serialize(report));

        Assert.Equal(PermitLimit + 2, statuses.Count);
    }
}
