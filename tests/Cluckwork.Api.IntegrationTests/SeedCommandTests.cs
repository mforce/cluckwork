namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

// #280 — `dotnet Cluckwork.Api.dll seed --profile demo` is a real CLI dispatch
// branch in Program.cs (args[0] == "seed"), never exercised by
// WebApplicationFactory<Program> — the testing host always passes empty args,
// so that branch is always skipped there. These tests spawn the actual built
// Cluckwork.Api.dll as a *subprocess*, the same binary and entry point an
// operator runs, so the dispatch code genuinely executes end to end (schema
// migrate, profile switch, exit before Kestrel).
//
// Own factory/container (own database), same reasoning as BaselineSeedFactory
// and DemoSeedTests: DemoDataSeeder writes to the fixed SeedDefaults.AccountId,
// so this must not share a database with anything else that seeds it.
public sealed class SeedCommandFixture : CluckworkWebApplicationFactory
{
    // Runtime-generated — never a hardcoded credential.
    public string AdminEmail { get; } = $"seedcmd-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Base data only (Account/Admin role/egg grades): scope 1a's CLI
        // dispatch deliberately migrates + runs only the requested profile's
        // seeder — it does not also invoke DatabaseSeeder (base provisioning
        // stays boot-only; #283 is the separate issue for touching that). So
        // this in-process host boots once, the unchanged startupScope base
        // seed runs, and the `seed --profile demo` *subprocess* under test
        // then runs against that already-provisioned database — exactly the
        // "boot the serving process once, then run `seed` against the same
        // database" flow described in Program.cs.
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
    }
}

public sealed class SeedCommandTests : IClassFixture<SeedCommandFixture>
{
    private readonly SeedCommandFixture _factory;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    public SeedCommandTests(SeedCommandFixture factory)
    {
        _factory = factory;
        // Forces host startup (idempotent/cached after the first call): the
        // base seed must have already run before any `seed --profile demo`
        // subprocess below depends on the account it creates.
        _ = _factory.Services;
    }

    private Process StartSeedCommand(string? profile, string environment = "Testing", string? connectionString = null)
    {
        var arguments = profile is null ? "seed" : $"seed --profile {profile}";
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // A deliberately minimal env — just what Program.cs reads eagerly
        // before the seed dispatch, plus what the demo profile itself needs.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__Default"] = connectionString ?? _factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return Process.Start(psi)!;
    }

    // Robust subprocess draining/timeout lives in the shared SeedCommandRunner
    // (#279 review — extracted so SimulationSeedCommandTests reuses it verbatim
    // instead of duplicating the pipe-deadlock/hang-detection logic).
    private Task<(int ExitCode, string Stdout, string Stderr)> RunSeedCommandAsync(
        string? profile, string environment = "Testing", string? connectionString = null) =>
        SeedCommandRunner.RunToCompletionAsync(
            StartSeedCommand(profile, environment, connectionString), SubprocessTimeout);

    [Fact]
    public async Task SeedCommand_Demo_SeedsDataAndExitsWithoutStartingKestrel()
    {
        var (exitCode, stdout, stderr) = await RunSeedCommandAsync("demo");
        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var flockCount = await db.Flocks.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
        Assert.Equal(3, flockCount);

        // Re-running is idempotent (DemoDataSeeder's own empty-catalog guard) —
        // exercise it through the same CLI path, not just the seeder directly.
        var (exitCode2, stdout2, stderr2) = await RunSeedCommandAsync("demo");
        Assert.True(0 == exitCode2, $"expected exit 0 on rerun, got {exitCode2}. stdout={stdout2} stderr={stderr2}");
        var flockCountAfterRerun = await db.Flocks.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
        Assert.Equal(3, flockCountAfterRerun);
    }

    [Fact]
    public async Task SeedCommand_UnknownProfile_ExitsNonZeroWithClearMessage()
    {
        var (exitCode, _, stderr) = await RunSeedCommandAsync("bogus-profile");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown or missing --profile", stderr);
    }

    [Fact]
    public async Task SeedCommand_MissingProfileFlag_ExitsNonZeroWithClearMessage()
    {
        var (exitCode, _, stderr) = await RunSeedCommandAsync(profile: null);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown or missing --profile", stderr);
    }

    // #280 prod guard (defense-in-depth): DemoDataSeeder is only registered
    // outside Production, so resolving it in a Production-env process must
    // fail with a clear operator-facing message — not an opaque DI exception.
    [Fact]
    public async Task SeedCommand_Demo_InProductionEnvironment_FailsCleanly_NotAnOpaqueDiException()
    {
        var (exitCode, _, stderr) = await RunSeedCommandAsync("demo", environment: "Production");

        Assert.Equal(1, exitCode);
        Assert.Contains("not available in Production", stderr);
        // The failure must be the translated message above, not a raw DI
        // resolution exception leaking to the operator's console.
        Assert.DoesNotContain("Unable to resolve service", stderr);
        Assert.DoesNotContain("No service for type", stderr);
    }

    // #284 review — a migrated-but-never-base-seeded database (the base
    // account/Admin role/egg grades that DatabaseSeeder creates on normal boot
    // don't exist here — nothing has ever booted this database as a serving
    // process). Own throwaway Postgres: this must NOT share _factory's
    // database, which SeedCommandFixture already base-seeds via
    // Seed:Enabled/AdminEmail/AdminPassword. The command applies migrations
    // itself, so the schema exists but the seed rows never do — exactly the
    // "migrated but unprovisioned" case the DemoDataSeeder preflight guards.
    [Fact]
    public async Task SeedCommand_Demo_BaseDataMissing_ExitsNonZeroWithClearMessage()
    {
        await using var freshDb = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await freshDb.StartAsync();

        var (exitCode, stdout, stderr) = await RunSeedCommandAsync(
            "demo", connectionString: freshDb.GetConnectionString());

        Assert.True(1 == exitCode, $"expected exit 1, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains("base data", stderr, StringComparison.OrdinalIgnoreCase);
    }
}
