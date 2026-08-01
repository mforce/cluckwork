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
        // The Testcontainers DB is plaintext; opt out of the #262 Production TLS floor so
        // the seed verb runs (it returns before the #260 serving guard, so only this is needed).
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

    // #283 review — supersedes the old "base data missing" demo test: since
    // roles/egg grades/the default account are now static reference data
    // baked into the migrations themselves (this command's own MigrateAsync
    // step provisions them), DemoDataSeeder's preflight can no longer
    // actually FIND them missing against ANY freshly migrated database — this
    // is the stronger guarantee that replaces it. Own throwaway, entirely
    // untouched Postgres — never migrated by anything before this subprocess
    // runs, so a green result here proves `seed --profile demo` needs nothing
    // but a connection string.
    [Fact]
    public async Task SeedCommand_Demo_AgainstAnUntouchedDatabase_MigratesAndSeedsInOneStep()
    {
        await using var freshDb = new PostgreSqlBuilder("postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await freshDb.StartAsync();

        var (exitCode, stdout, stderr) = await RunSeedCommandAsync(
            "demo", connectionString: freshDb.GetConnectionString());

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
    }
}
