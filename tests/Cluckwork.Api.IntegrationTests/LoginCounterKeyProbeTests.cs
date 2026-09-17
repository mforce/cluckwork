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

    private const string LoopbackKeyPattern = "*auth-login:127.0.0.1*";

    // The burst's own bucket, so the counter it reads is the one its own requests
    // wrote.
    private static string BurstKeyPattern =>
        $"*auth-login:{IsolatedLoginBucket.ClientIpFor(nameof(LoginCounterKeyProbeTests))}*";

    // Above the shipped login PermitLimit (10 per 900 s), so the burst always
    // crosses the budget and the report shows where the 429s start.
    private const int PermitBurst = 14;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();

    private readonly RedisContainer _redis =
        new RedisBuilder("redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2").Build();

    // The container is private today; keep the host namespace private too, so a
    // future shared fixture cannot silently reintroduce another counter owner.
    private readonly string _redisKeyNamespace = $"cluckwork-test-{Guid.NewGuid():N}";

    private IConnectionMultiplexer _mux = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _mux.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    // "{cluckwork:win:auth-login:127.0.0.1}:<bucket>" — the braces are the cluster
    // hash tag RedisFixedWindowCounter adds, and the bucket is floor(server-ms /
    // window), so a 900 s window turns over every fifteen minutes. Scanned rather
    // than read by name, so a drift in namespace, prefix, or hash-tag shape shows
    // up as "no keys" instead of a false zero.
    //
    // Two patterns, not one: the loopback bucket is the finding and the burst bucket
    // is the measurement. A single pattern covering every auth-login key would fold
    // the suite's traffic into the burst's own arithmetic.
    private Task<Dictionary<string, long>> ReadLoopbackKeysAsync() => ReadKeysAsync(LoopbackKeyPattern);

    private Task<Dictionary<string, long>> ReadBurstKeysAsync() => ReadKeysAsync(BurstKeyPattern);

    private async Task<Dictionary<string, long>> ReadKeysAsync(string pattern)
    {
        var db = _mux.GetDatabase();
        var found = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var endpoint in _mux.GetEndPoints())
        {
            var server = _mux.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: pattern))
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
        psi.ArgumentList.Add(typeof(Program).Assembly.Location);

        // "Testing", not "Development": a spawned process must not pick up the
        // developer's local user-secrets (same reason CluckworkWebApplicationFactory
        // pins it).
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        // The same pinned image MultiInstanceRateLimitTests uses. The probe does not
        // query it; it exists because Program.cs migrates before the pipeline is
        // built, so a serving process cannot reach its request pipeline at all
        // without a database it can talk to.
        psi.Environment["ConnectionStrings__Default"] = _postgres.GetConnectionString();
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Database__MigrateOnStartup"] = "false";
        psi.Environment["SharedState__Redis__ConnectionString"] = _redis.GetConnectionString();
        psi.Environment["SharedState__Redis__KeyNamespace"] = _redisKeyNamespace;
        // #840 — loopback is a trusted proxy here so the burst can be given its own
        // client address, and the counter the probe reads is the one its own requests
        // wrote rather than one shared with every other class on this machine. The
        // foreign-spend reading is taken from the loopback bucket and deliberately
        // NOT isolated: that reading is the finding.
        psi.Environment["RateLimiting__TrustedProxies__0"] = "127.0.0.1/32";
        // 24h window: the bucket boundary is wall-clock inside the limiter script,
        // and a boundary crossing mid-burst resets the count, so the burst's own
        // 429s can vanish (#840's 2026-09-16 CI specimens).
        psi.Environment["RateLimiting__Login__WindowSeconds"] = "86400";
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
        // reports unhealthy until the database is migrated, and nothing here migrates
        // it. The limiter runs before the endpoint (RequireRateLimiting on
        // /auth/login, UseRateLimiter ahead of routing), so polling the login route
        // sees the first increment the moment the policy is live.
        //
        // The finding, read before this class's child exists. Nothing clears the
        // bucket first: this Redis container is private to the probe, so whatever is
        // in the loopback bucket here was spent by this class's own fixture on boot.
        // That IS the collision, and clearing it would delete the evidence.
        var before = await ReadLoopbackKeysAsync();

        await using var child = ServingSubprocess.Start(MakeStartInfo(), ServingSubprocess.FreeTcpPort());
        using var http = new HttpClient
        {
            BaseAddress = child.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        };
        // The burst's own bucket. The child trusts loopback as a proxy (see
        // MakeStartInfo), so this header decides the key.
        http.DefaultRequestHeaders.Add("X-Forwarded-For", IsolatedLoginBucket.ClientIpFor(nameof(LoginCounterKeyProbeTests)));

        async Task<HttpStatusCode> PostLoginAsync()
        {
            using var response = await http.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { farmCode = TestHarness.DefaultFarmCode, email = "nobody@example.com", password = "WrongPassw0rd!" });
            return response.StatusCode;
        }

        // The readiness question and the spending question are separate, and an
        // earlier version of this probe conflated them. 429 does NOT mean the
        // pipeline is live: the bucket is keyed on the loopback address alone, so
        // another class running concurrently can have exhausted it before this
        // child bound its port. Treating 429 as ready made the probe burst into a
        // spent bucket and report fourteen refusals as if they were its own result.
        //
        // The budget is cleared rather than waited out, and that is only legitimate
        // because the reading above was already taken. Without the clear, a bucket
        // another class had already spent turns all fourteen requests into refusals
        // and the probe reports someone else's spend as its own result — which is
        // what an earlier version of this file did.
        var statuses = new List<HttpStatusCode>();
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (statuses.Count == 0)
        {
            try
            {
                var status = await PostLoginAsync();
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.InternalServerError)
                {
                    // The request passed the limiter and reached the handler: 401 is
                    // an unknown user, 500 is the handler failing on the empty schema
                    // this child never migrates. Which one is not the probe's
                    // question — the counter incremented either way.
                    statuses.Add(status);
                }
                else
                {
                    Console.WriteLine($"CLUCKWORK_840_PROBE still warming up: {status}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            Assert.True(DateTime.UtcNow < deadline,
                $"the login policy never let a request through within {ReadyTimeout.TotalSeconds:0} s");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        for (var i = 1; i < PermitBurst; i++)
        {
            statuses.Add(await PostLoginAsync());
        }

        var after = await ReadBurstKeysAsync();

        var report = new
        {
            test = nameof(Probe_login_counter_key_across_the_loopback_bucket),
            permitBurst = PermitBurst,
            observedStatuses = statuses.Select(s => (int)s).ToArray(),
            // Foreign spend: read before this class's child existed, so nothing the
            // probe itself does can be in it.
            loopbackKeysBefore = before,
            foreignSpendOnLoopback = before.Sum(kv => kv.Value),
            burstKeysAfter = after,
            spendOnBurstBucket = after.Sum(kv => kv.Value),
            // A bucket rollover mid-burst would account for a delta larger than the
            // burst with no foreign writer involved.
            burstBucketCount = after.Count,
        };

        Console.WriteLine("CLUCKWORK_840_PROBE " + JsonSerializer.Serialize(report));

        // The counter is what is under test, so it is what is asserted: the shared
        // Redis key moved by at least the burst, which an in-process fallback never
        // writes and an uninvoked policy never touches.
        //
        // NOT asserted exact. The Redis container is private to this class, so the
        // increments beyond the burst cannot have come from another class — they came
        // from this class's own fixture, which builds its TestServer in the
        // constructor before any test body runs. A fixture spending against the budget
        // its own test then measures is #840's collision one level down, and it is why
        // an exact assertion here would be red on a working mechanism.
        var spend = after.Sum(kv => kv.Value);
        Assert.Equal(PermitBurst, statuses.Count);
        Assert.True(spend >= PermitBurst,
            $"{PermitBurst} requests through the login policy left only {spend} increments "
            + "on the Redis key — the counter fell back to in-process, or the policy did not run");
        Assert.Single(after);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
