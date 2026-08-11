namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

// #279 review Fix 5 (codex) — the `simulation` profile of the `seed --profile
// <name>` CLI dispatch, exercised end-to-end as a SUBPROCESS (the real built
// Cluckwork.Api.dll, the same binary/entry point an operator runs), exactly as
// SeedCommandTests already does for the demo profile. WebApplicationFactory
// never passes args, so the dispatch branch (args[0] == "seed") is otherwise
// never covered: the new simulation case, its Production null-guard, the
// migrate-then-seed-then-exit ordering, the exit codes, and the on-disk manifest
// flow all need this to be tested at all.
//
// Own factory/container, same reasoning as SeedCommandTests: the simulation
// seeder writes to the fixed SeedDefaults.AccountId, so it must not share a
// database with anything else that seeds it.
//
// #283 — the default account/Admin role/egg grades ship as migration-baked
// static reference data (no Seed:* config, no runtime seeder); the Owner
// admin itself does not, so it's seeded in-process here — standing in for a
// real `bootstrap-admin` run — before the `seed --profile simulation`
// subprocess under test runs against the same already-provisioned database.
public sealed class SimulationSeedCommandFixture : CluckworkWebApplicationFactory, IAsyncLifetime
{
    // Runtime-generated — never a hardcoded credential.
    public string AdminEmail { get; } = $"simcmd-{Guid.NewGuid():N}@test.local";
    public string CastPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    // NOTE: redeclaring `IAsyncLifetime` is required for xUnit to dispatch to
    // THIS override (same reasoning as SimulationSeedFactory).
    public new async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await this.SeedUserAsync(
            Cluckwork.Domain.Accounts.SeedDefaults.AccountId, AdminEmail, Cluckwork.Domain.Accounts.Roles.Owner);
    }
}

public sealed class SimulationSeedCommandTests : IClassFixture<SimulationSeedCommandFixture>
{
    private readonly SimulationSeedCommandFixture _factory;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    // Longer than the demo suite's: the simulation seed drives O(HistoryDays *
    // flocks) real handler round-trips against Postgres.
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(120);

    // Shallow like SimulationSeedFactory — fast but still clears MinSentinelAgeDays.
    private const string HistoryDays = "12";
    private const string TimeZoneId = "America/Chicago";

    public SimulationSeedCommandTests(SimulationSeedCommandFixture factory)
    {
        _factory = factory;
        // Forces host startup (idempotent/cached): the base seed must have run
        // before any `seed --profile simulation` subprocess depends on it.
        _ = _factory.Services;
    }

    private Process StartSeedCommand(
        string environment = "Testing", string? connectionString = null, string? manifestPath = null)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" seed --profile simulation")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__Default"] = connectionString ?? _factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        // The Testcontainers DB is plaintext; opt out of the #262 Production TLS floor so the
        // simulation seed verb runs. Only this one: the #260/#319 serving guards
        // check ProcessRole and skip a one-shot verb (#347), and the TLS floor
        // applies to both roles by design.
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        // #283 — the simulation preflight now looks up the reused Owner by
        // ROLE membership in the default account, not by a configured email
        // (Seed:AdminEmail is retired along with the runtime seeder that fed
        // it) — the fixture's InitializeAsync already seeded that Owner
        // in-process, so the subprocess needs nothing further to find it.
        // The simulation profile's own config (its own "Simulation" section) —
        // no Seed:Simulation gate exists anymore (#279); the command IS the gate.
        psi.Environment["Simulation__CastPassword"] = _factory.CastPassword;
        psi.Environment["Simulation__TimeZoneId"] = TimeZoneId;
        psi.Environment["Simulation__HistoryDays"] = HistoryDays;
        if (manifestPath is not null)
            psi.Environment["Simulation__CredentialOutputPath"] = manifestPath;
        return Process.Start(psi)!;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunSeedCommandAsync(
        string environment = "Testing", string? connectionString = null, string? manifestPath = null) =>
        SeedCommandRunner.RunToCompletionAsync(
            StartSeedCommand(environment, connectionString, manifestPath), SubprocessTimeout);

    [Fact]
    public async Task SeedCommand_Simulation_SeedsDataWritesCompleteManifest_AndIsIdempotent()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"simcmd-manifest-{Guid.NewGuid():N}.json");
        try
        {
            var (exitCode, stdout, stderr) = await RunSeedCommandAsync(manifestPath: manifestPath);
            Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");

            // The on-disk manifest the #243 findings header / #277 suite read.
            var manifest = await ReadManifestAsync(manifestPath);
            Assert.True(manifest.Complete);
            Assert.False(string.IsNullOrWhiteSpace(manifest.Fingerprint));

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accountCount = await db.Accounts.IgnoreQueryFilters().CountAsync();
            Assert.Equal(2, accountCount); // primary + the deterministic second sim account.

            // Idempotent through the SAME CLI path — a rerun converges (exit 0),
            // never mints a third account, and re-writes an identical manifest.
            var (exitCode2, stdout2, stderr2) = await RunSeedCommandAsync(manifestPath: manifestPath);
            Assert.True(0 == exitCode2, $"expected exit 0 on rerun, got {exitCode2}. stdout={stdout2} stderr={stderr2}");
            var accountCountAfterRerun = await db.Accounts.IgnoreQueryFilters().CountAsync();
            Assert.Equal(2, accountCountAfterRerun);

            var manifestAfterRerun = await ReadManifestAsync(manifestPath);
            Assert.Equal(manifest.Fingerprint, manifestAfterRerun.Fingerprint);
        }
        finally
        {
            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { /* cleanup only */ }
        }
    }

    // #280 prod guard (defense-in-depth): SimulationDataSeeder is only registered
    // outside Production, so resolving it in a Production-env process must fail
    // with a clear operator-facing message — not an opaque DI exception. This
    // check runs BEFORE MigrateAsync, so the guard fires regardless of DB state.
    [Fact]
    public async Task SeedCommand_Simulation_InProductionEnvironment_FailsCleanly_NotAnOpaqueDiException()
    {
        var (exitCode, _, stderr) = await RunSeedCommandAsync(environment: "Production");

        Assert.Equal(1, exitCode);
        Assert.Contains("not available in Production", stderr);
        Assert.DoesNotContain("Unable to resolve service", stderr);
        Assert.DoesNotContain("No service for type", stderr);
    }

    // A migrated-but-never-base-seeded database (the base account/Admin role/egg
    // grades DatabaseSeeder creates on normal boot don't exist here). Own
    // throwaway Postgres: must NOT share _factory's already-base-seeded database.
    // The command migrates the schema itself, so tables exist but the seed rows
    // never do — exactly the "migrated but unprovisioned" case the preflight
    // guards, reported as a clean non-zero exit rather than a mid-seed throw.
    [Fact]
    public async Task SeedCommand_Simulation_BaseDataMissing_ExitsNonZeroWithClearMessage()
    {
        await using var freshDb = new PostgreSqlBuilder("postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await freshDb.StartAsync();

        var (exitCode, stdout, stderr) = await RunSeedCommandAsync(
            connectionString: freshDb.GetConnectionString());

        Assert.True(1 == exitCode, $"expected exit 1, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains("prerequisites missing", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SimulationManifest> ReadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<SimulationManifest>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {path}.");
    }
}
