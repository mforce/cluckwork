namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

// #263 — `dotnet Cluckwork.Api.dll migrate` is a real CLI dispatch branch in
// Program.cs (args[0] == "migrate"), never exercised by WebApplicationFactory
// (empty args). It is the pre-deploy-job entrypoint that lets production run
// schema DDL under a dedicated migrator credential with
// Database:MigrateOnStartup=false, so the serving process never applies DDL.
// These spawn the actual built Cluckwork.Api.dll as a subprocess (the same
// binary/entry point a deploy job runs) against a FRESH, unmigrated Postgres.
public sealed class MigrateCommandTests
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(120);

    private static Process StartMigrateCommand(string connectionString, string environment = "Production")
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" migrate")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__Default"] = connectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return Process.Start(psi)!;
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string connectionString) =>
        SeedCommandRunner.RunToCompletionAsync(
            StartMigrateCommand(connectionString), SubprocessTimeout);

    // Runs in the PRODUCTION environment on purpose: the migrate job runs against
    // a real production database, and appsettings.Production.json sets
    // MigrateOnStartup=false — the command must migrate regardless of that flag.
    [Fact]
    public async Task Migrate_AppliesSchemaToAFreshDatabase_ThenIsIdempotent()
    {
        await using var db = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await db.StartAsync();
        var cs = db.GetConnectionString();

        // Fresh, unmigrated database: the command applies the schema and exits 0.
        var (exit1, out1, err1) = await RunAsync(cs);
        Assert.True(0 == exit1, $"expected exit 0, got {exit1}. stdout={out1} stderr={err1}");

        // The schema now exists — a table only a migration creates is present.
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('public.\"AspNetUsers\"') IS NOT NULL", conn);
            var created = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.True(created, "migrate should have created the schema (AspNetUsers table missing)");
        }

        // Re-run against the now-current database: still exit 0, and it took the
        // no-op branch (proves idempotency, not merely "the rerun didn't crash").
        var (exit2, out2, err2) = await RunAsync(cs);
        Assert.True(0 == exit2, $"expected exit 0 on rerun, got {exit2}. stdout={out2} stderr={err2}");
        Assert.Contains("already current", out2);
    }

    [Fact]
    public async Task Migrate_UnreachableDatabase_ExitsNonZeroWithMessage()
    {
        // A connection to a port with nothing listening: MigrateAsync/GetPending
        // throws, which the command maps to a clean stderr message + exit 1
        // (fail-loud), not an unhandled crash.
        var (exitCode, _, stderr) = await RunAsync(
            "Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=y;Timeout=3;Command Timeout=3");

        Assert.Equal(1, exitCode);
        Assert.Contains("Migrate failed", stderr);
    }
}
