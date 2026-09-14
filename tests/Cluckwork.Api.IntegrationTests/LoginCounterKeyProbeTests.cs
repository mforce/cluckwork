namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using StackExchange.Redis;
using Testcontainers.Redis;

// #840 — the census reproduced "expected 429, got 401" once in 36 runs and then
// got 0 failures in 80 instrumented runs, so it had the flake and not the trace.
// The reason the instrumentation proved nothing is that it recorded what the
// counter was ASKED to count and never who asked. The login bucket key is
// "auth-login:<client-ip>" and nothing else (RateLimitKey.ForClient), so a
// request from ANY concurrently running test class that presents the loopback
// address spends the same budget this test is measuring — and several classes
// do: FakeRemoteIpStartupFilter rewrites Connection.RemoteIpAddress only when
// the X-Test-Remote header is present, so a login that omits it keeps the real
// socket peer, 127.0.0.1.
//
// This probe reads the SHARED key directly instead of inferring it from a
// response code. It is deliberately not an assertion about production behaviour:
// the budget-sharing it measures is correct by design (#544) and the collision
// is a property of the test suite, not of the limiter. It exists to answer one
// question with a number — when this test's budget is wrong, is the count wrong
// because something else spent it, or because the counter fell back?
//
// It runs in its own collection (no [Collection] attribute) exactly like
// MultiInstanceRateLimitTests, so it sees the same concurrency the flake does.
public sealed class LoginCounterKeyProbeTests : IAsyncLifetime
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SubprocessExitTimeout = TimeSpan.FromSeconds(30);

    // The same window the flaky test configures. The bucket is derived from
    // Redis's own clock as floor(epoch_ms / window), so the probe can compute
    // the live key name without knowing anything about the counter's internals.
    private const int WindowSeconds = 900;
    private const int PermitLimit = 5;

    // The same pinned image MultiInstanceRateLimitTests and
    // SharedState/RedisFixture.cs use, verbatim.
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

    // The key the limiter counts: "{cluckwork:auth-login:127.0.0.1}:<bucket>",
    // with the braces being the cluster hash tag RedisFixedWindowCounter adds.
    // Read by SCAN rather than by name, so a drift in the namespace or the hash
    // tag shape shows up as "no keys" instead of a false zero.
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
        // No database is needed to reach the limiter — the login policy runs
        // before any account lookup (#544) — but the serving boot resolves the
        // connection string, so it points at nothing and never gets used.
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
                new { farmCode = Infrastructure.TestHarness.DefaultFarmCode, email = "nobody@example.com", password = "WrongPassw0rd!" });
            statuses.Add(response.StatusCode);
        }

        var after = await ReadLoginKeysAsync();

        // The probe's own budget: PermitLimit requests admitted, then 429s. A
        // failure HERE is the collision, observed on purpose — some other
        // collection spent this bucket while this test was running.
        var foreignBefore = before.Sum(kv => kv.Value);
        var foreignDuring = after.Where(kv => !before.TryGetValue(kv.Key, out var was))
            .Sum(kv => kv.Value);

        var report = new
        {
            test = nameof(Probe_login_counter_key_across_the_loopback_bucket),
            permitLimit = PermitLimit,
            windowSeconds = WindowSeconds,
            observedStatuses = statuses.Select(s => (int)s).ToArray(),
            keysBefore = before,
            keysAfter = after,
            // Anything above PermitLimit on a key this test did not create is a
            // foreign spender; a count of exactly PermitLimit+1 with a non-429
            // status is the fallback path (ResilientFixedWindowCounter caught a
            // Redis error and counted locally instead).
            spendBeforeThisTest = foreignBefore,
            spendOnNewKeys = foreignDuring,
        };

        // Emitted rather than asserted: this is the instrument the census was
        // missing, and a green run is information too. CI captures it with
        // --logger "console;verbosity=detailed".
        Console.WriteLine("CLUCKWORK_840_PROBE " + JsonSerializer.Serialize(report));

        Assert.Equal(PermitLimit + 2, statuses.Count);
    }
}
