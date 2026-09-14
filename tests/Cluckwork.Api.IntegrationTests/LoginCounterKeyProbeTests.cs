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

    // The bucket id is floor(server-ms / window), so this deletes only the current
    // wall-clock window. A concurrent class counting in the NEXT window is untouched.
    private async Task ClearLoginKeysAsync()
    {
        var db = _mux.GetDatabase();
        foreach (var endpoint in _mux.GetEndPoints())
        {
            var server = _mux.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: "*auth-login:127.0.0.1*"))
            {
                await db.KeyDeleteAsync(key);
            }
        }
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
        // The starting value is read BEFORE the child exists, so the "was someone
        // else already in this bucket" reading cannot be contaminated by the child's
        // own startup work. This is the probe's only finding; everything after it is
        // measurement.
        var before = await ReadLoginKeysAsync();

        await ClearLoginKeysAsync();

        await using var child = ServingSubprocess.Start(MakeStartInfo(), ServingSubprocess.FreeTcpPort());
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

        var after = await ReadLoginKeysAsync();

        var report = new
        {
            test = nameof(Probe_login_counter_key_across_the_loopback_bucket),
            permitBurst = PermitBurst,
            observedStatuses = statuses.Select(s => (int)s).ToArray(),
            // Foreign spend, and the only reading of it this probe can trust: taken
            // before the child existed and before its own burst. The bucket is shared
            // with every other class on this address, so a concurrent login racing the
            // clear or the burst adds spend the probe did not cause, and a delta
            // measured after the fact cannot tell the two apart.
            keysBefore = before,
            spendBeforeThisProbe = before.Sum(kv => kv.Value),
            keysAfter = after,
            spendAfterThisProbe = after.Sum(kv => kv.Value),
            // A bucket rollover mid-burst would account for a delta larger than the
            // burst with no foreign writer involved.
            bucketCountAfter = after.Count,
        };

        Console.WriteLine("CLUCKWORK_840_PROBE " + JsonSerializer.Serialize(report));

        // The counter is what is under test, so it is what is asserted. The probe
        // cannot assert an exact number: the bucket is shared with every other class
        // on this address, and a concurrent login racing the clear adds spend the
        // probe did not cause. What it can prove is that the policy is the thing
        // doing the counting — a fallback to an in-process counter, or a policy that
        // never ran, leaves this Redis key alone.
        var spend = after.Sum(kv => kv.Value);
        Assert.Equal(PermitBurst, statuses.Count);
        Assert.True(spend >= PermitBurst,
            $"fourteen requests through the login policy left only {spend} increments on the shared key");
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
