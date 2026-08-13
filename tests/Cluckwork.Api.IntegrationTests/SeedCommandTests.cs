namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
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
// Own factory/container (own database), same reasoning as DemoSeedTests:
// DemoDataSeeder writes to the fixed SeedDefaults.AccountId, so this must not
// share a database with anything else that seeds it.
public sealed class SeedCommandTests : IClassFixture<CluckworkWebApplicationFactory>
{
    private readonly CluckworkWebApplicationFactory _factory;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    public SeedCommandTests(CluckworkWebApplicationFactory factory)
    {
        _factory = factory;
        // Forces host + Postgres container startup (schema migrated —
        // #283's base reference data ships as part of THAT) before any `seed
        // --profile demo` subprocess below depends on it.
        _ = _factory.Services;
    }

    private Process StartSeedCommand(string? profile, string environment = "Testing", string? connectionString = null) =>
        StartVerb(profile is null ? "seed" : $"seed --profile {profile}", environment, connectionString);

    // #500 — the demo profile now requires an Owner, which only `bootstrap-admin`
    // provisions, so these tests must drive a second verb against the same
    // database. Same binary, same environment; only the arguments differ.
    private Process StartVerb(string arguments, string environment = "Testing", string? connectionString = null)
    {
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
        // The Testcontainers DB is plaintext; opt out of the #262 Production TLS floor so
        // the seed verb runs. Only this one: the #260/#319 serving guards check
        // ProcessRole and skip a one-shot verb (#347), and the TLS floor applies
        // to both roles by design.
        psi.Environment["Database__AllowInsecureConnection"] = "true";
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

    // #500 — provisions the Owner the demo profile now requires. A re-run is a
    // documented no-op, so tests may call this freely.
    private async Task BootstrapAdminAsync(string? connectionString = null)
    {
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            StartVerb($"bootstrap-admin --email admin-{Guid.NewGuid():N}@test.local",
                connectionString: connectionString),
            SubprocessTimeout);
        Assert.True(0 == exitCode, $"bootstrap-admin failed ({exitCode}). stdout={stdout} stderr={stderr}");
    }

    [Fact]
    public async Task SeedCommand_Demo_SeedsDataAndExitsWithoutStartingKestrel()
    {
        // #500 — the demo fixture is signed by the account's Owner, so one must
        // exist first. This is the documented operator flow (README): migrate,
        // bootstrap-admin, then seed.
        await BootstrapAdminAsync();

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

    // #283 review — supersedes the old "base data missing" demo test: since
    // roles/egg grades/the default account are now static reference data
    // baked into the migrations themselves (this command's own MigrateAsync
    // step provisions them), DemoDataSeeder's preflight can no longer
    // actually FIND them missing against ANY freshly migrated database.
    //
    // #500 CHANGED WHAT THIS PINS. It used to assert that a green result
    // "proves `seed --profile demo` needs nothing but a connection string".
    // That property is deliberately gone: the demo fixture is now signed by the
    // account's Owner, and an Owner comes only from `bootstrap-admin`. The
    // reasoning, recorded here because this comment is where the old promise
    // lived: a demo fixture exists to be looked at, looking requires a login,
    // and a login requires an Owner — so requiring one up front converts a
    // later surprise into an immediate, clearly-worded failure.
    //
    // The one-step migrate-and-run guarantee is still real and still tested,
    // in two halves against an entirely untouched Postgres: without an Owner
    // the command fails on its own PREREQUISITE (proving migration ran — a
    // broken migration would fail differently, and the #283 base data would be
    // missing instead), and with one it seeds in a single further step.
    [Fact]
    public async Task SeedCommand_Demo_AgainstAnUntouchedDatabase_MigratesAndSeedsInOneStep()
    {
        await using var freshDb = new PostgreSqlBuilder("postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await freshDb.StartAsync();

        var withoutOwner = await RunSeedCommandAsync(
            "demo", connectionString: freshDb.GetConnectionString());
        Assert.True(1 == withoutOwner.ExitCode,
            $"expected exit 1 without an Owner, got {withoutOwner.ExitCode}. " +
            $"stdout={withoutOwner.Stdout} stderr={withoutOwner.Stderr}");
        Assert.Contains("bootstrap-admin", withoutOwner.Stderr);

        await BootstrapAdminAsync(freshDb.GetConnectionString());

        var (exitCode, stdout, stderr) = await RunSeedCommandAsync(
            "demo", connectionString: freshDb.GetConnectionString());

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
    }
}
