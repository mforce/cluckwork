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

    private Process StartSeedCommand(
        string? profile, string environment = "Testing", string? connectionString = null, string? farmCode = null)
    {
        var arguments = profile is null ? "seed" : $"seed --profile {profile}";
        if (farmCode is not null)
            arguments += $" --farm-code {farmCode}";
        return StartVerb(arguments, environment, connectionString);
    }

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
        string? profile, string environment = "Testing", string? connectionString = null,
        string? farmCode = null) =>
        SeedCommandRunner.RunToCompletionAsync(
            StartSeedCommand(profile, environment, connectionString, farmCode), SubprocessTimeout);

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
        // exist first. This is the documented operator flow
        // (docs/runbooks/first-admin-provisioning.md): migrate, bootstrap-admin,
        // then seed.
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

    // `seed --profile demo --farm-code <slug>` — the README-capture path. The sim
    // harness provisions a second, small farm and seeds THAT with the demo
    // profile, because the simulation fixture's 100 never-filing catalog flocks
    // leave every day partial and the dashboard's trend strip with no complete
    // day to scale against.
    //
    // Its own Postgres, not the class fixture's, and that is the assertion rather
    // than tidiness: "nothing landed under the DEFAULT farm" is only meaningful on
    // a database no sibling [Fact] has demo-seeded. xUnit runs the facts in this
    // class sequentially but in no guaranteed ORDER, so on the shared container
    // the sibling above would decide this one's verdict.
    //
    // Row counts come from raw SQL rather than a DbContext because the only
    // handle on this database is a connection string — the class fixture's
    // services are wired to a different container entirely.
    [Fact]
    public async Task SeedCommand_Demo_WithFarmCode_SeedsThatFarmAndLeavesTheDefaultEmpty()
    {
        await using var freshDb = new PostgreSqlBuilder("postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await freshDb.StartAsync();
        var connectionString = freshDb.GetConnectionString();

        // provision-account does NOT migrate (it runs as the least-privilege
        // runtime role in production), so the schema has to exist first.
        var migrated = await SeedCommandRunner.RunToCompletionAsync(
            StartVerb("migrate", connectionString: connectionString), SubprocessTimeout);
        Assert.True(0 == migrated.ExitCode,
            $"migrate failed ({migrated.ExitCode}). stdout={migrated.Stdout} stderr={migrated.Stderr}");

        const string slug = "readme-capture";
        var ownerEmail = $"owner-{Guid.NewGuid():N}@test.local";
        var provisioned = await SeedCommandRunner.RunToCompletionAsync(
            StartVerb(
                $"provision-account --slug {slug} --name \"Capture Farm\" --owner-email {ownerEmail}",
                connectionString: connectionString),
            SubprocessTimeout);
        Assert.True(0 == provisioned.ExitCode,
            $"provision-account failed ({provisioned.ExitCode}). " +
            $"stdout={provisioned.Stdout} stderr={provisioned.Stderr}");
        var accountId = AccountIdFrom(provisioned.Stdout);

        var (exitCode, stdout, stderr) = await RunSeedCommandAsync(
            "demo", connectionString: connectionString, farmCode: slug);
        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");

        // Three flocks under the NAMED farm, and — the half that would have gone
        // unnoticed if the flag were ignored — none at all under the default one.
        Assert.Equal(3, await FlockCountAsync(connectionString, accountId));
        Assert.Equal(0, await FlockCountAsync(connectionString, SeedDefaults.AccountId));

        // The provisioned farm's Owner is the one AccountProvisioner created, so
        // no bootstrap-admin run is needed for it — asserted by the exit 0 above,
        // since the demo profile refuses to seed an ownerless account (#500).
        //
        // Re-running converges on the same three flocks (the seeder's own
        // empty-catalog guard is per account already).
        var rerun = await RunSeedCommandAsync("demo", connectionString: connectionString, farmCode: slug);
        Assert.True(0 == rerun.ExitCode,
            $"expected exit 0 on rerun, got {rerun.ExitCode}. stdout={rerun.Stdout} stderr={rerun.Stderr}");
        Assert.Equal(3, await FlockCountAsync(connectionString, accountId));
    }

    // An unknown code must fail BEFORE any seeding, and the message must name the
    // verb that lists the codes this database actually holds — otherwise the
    // operator's next move is to guess.
    [Fact]
    public async Task SeedCommand_Demo_WithUnknownFarmCode_ExitsNonZeroAndNamesListAccounts()
    {
        var (exitCode, _, stderr) = await RunSeedCommandAsync("demo", farmCode: "no-such-farm");

        Assert.Equal(1, exitCode);
        Assert.Contains("No farm with code 'no-such-farm'", stderr);
        Assert.Contains("list-accounts", stderr);
    }

    // The simulation profile is default-farm-only by design. Refusing the flag is
    // the whole point: honouring it would seed a fixture whose manifest, cast
    // emails and pinned counts all describe the default farm, into a farm that is
    // none of those things — and report exit 0.
    [Fact]
    public async Task SeedCommand_Simulation_WithFarmCode_IsRefused()
    {
        var (exitCode, _, stderr) = await RunSeedCommandAsync("simulation", farmCode: "readme-capture");

        Assert.Equal(1, exitCode);
        Assert.Contains("does not accept --farm-code", stderr);
    }

    // provision-account prints "Farm provisioned: <slug> (account <guid>); Owner <email>."
    // — the id is not obtainable any other way (list-accounts prints code, name
    // and status, deliberately not the id), so the assertion above depends on
    // that line's shape and says so by failing here rather than three lines later.
    private static Guid AccountIdFrom(string provisionStdout)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            provisionStdout, @"\(account ([0-9a-fA-F-]{36})\)");
        Assert.True(match.Success,
            $"provision-account's stdout no longer carries '(account <guid>)': {provisionStdout}");
        return Guid.Parse(match.Groups[1].Value);
    }

    private static async Task<int> FlockCountAsync(string connectionString, Guid accountId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM \"Flocks\" WHERE \"AccountId\" = @accountId";
        command.Parameters.AddWithValue("accountId", accountId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}
