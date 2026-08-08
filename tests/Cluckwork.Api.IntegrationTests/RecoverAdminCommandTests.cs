namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// #265 — the `recover-admin` break-glass command is a real CLI dispatch branch
// in Program.cs (args[0] == "recover-admin"), never exercised by
// WebApplicationFactory (which passes empty args). These spawn the actual built
// Cluckwork.Api.dll as a subprocess — the same binary/entry point an operator
// runs — in the PRODUCTION environment, proving break-glass is deliberately NOT
// env-gated (unlike the demo/simulation seed profiles). Own factory/container:
// the command resets the seeded admin's password, which must not disturb another
// suite that shares a database.
public sealed class RecoverAdminCommandTests : IClassFixture<BreakGlassRecoveryFixture>
{
    private readonly BreakGlassRecoveryFixture _factory;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    public RecoverAdminCommandTests(BreakGlassRecoveryFixture factory)
    {
        _factory = factory;
        // Forces host startup (idempotent/cached): the base seed must have run so
        // the admin the subprocess recovers already exists in the shared database.
        _ = _factory.Services;
    }

    private Process StartRecoverCommand(
        string arguments, string environment, IDictionary<string, string>? extraEnvironment = null)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" recover-admin {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__Default"] = _factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        // The Testcontainers DB is plaintext; opt out of the #262 Production TLS floor so
        // recover-admin runs (it returns before the #260 serving guard, so only this is needed).
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
            psi.Environment[key] = value;
        return Process.Start(psi)!;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string arguments,
        string environment = "Production",
        IDictionary<string, string>? extraEnvironment = null) =>
        SeedCommandRunner.RunToCompletionAsync(
            StartRecoverCommand(arguments, environment, extraEnvironment), SubprocessTimeout);

    [Fact]
    public async Task RecoverAdmin_InProduction_ResetsPassword_PrintsWorkingTemporaryPassword()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            $"--email {_factory.AdminEmail} --reason integration-drill");

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains("Temporary password:", stdout);
        var tempPassword = ExtractTemporaryPassword(stdout);
        // #273 — the actual "never the logger" guarantee, asserted directly:
        // Serilog's own Console sink ALSO writes to this SAME captured stdout
        // stream (running in Production here, this subprocess also legitimately
        // logs the #262 "INSECURE database connection explicitly permitted"
        // warning — a real Serilog line, correctly present, NOT a regression),
        // so a stray password-carrying log line would show up here, not vanish.
        // The precise invariant: the password appears EXACTLY ONCE in the whole
        // capture (the one explicit Console.Out line), and never inside a line
        // that opens with a Serilog outputTemplate "[HH:mm:ss LVL]" bracket.
        Assert.Equal(1, CountOccurrences(stdout, tempPassword));
        Assert.DoesNotContain(
            stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.TrimStart().StartsWith('[') && line.Contains(tempPassword));

        // The printed one-time password must actually work, and the old one must not.
        using var scope = _factory.Services.CreateScope();
        var idp = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        Assert.True((await idp.LoginAsync(_factory.AdminEmail, tempPassword)).IsSuccess,
            "the printed temporary password should log in");
        Assert.True((await idp.LoginAsync(_factory.AdminEmail, _factory.AdminPassword)).IsFailure,
            "the old seeded password must be rejected after recovery");
    }

    // #316 review — the OTLP guard validates during service registration, which
    // runs BEFORE CliDispatcher. Enforcing it for a one-off verb meant a
    // plaintext Otlp:Endpoint aborted recover-admin with an unhandled exception
    // (SIGABRT, exit 134) — an unrelated telemetry setting blocking the
    // break-glass path for a locked-out farm (#265), which is worse than the
    // leak the guard prevents. The verb must run; export is simply disabled.
    [Fact]
    public async Task RecoverAdmin_WithPlaintextOtlpEndpoint_StillRecovers_ExportDisabled()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            $"--email {_factory.AdminEmail} --reason otlp-guard-drill",
            extraEnvironment: new Dictionary<string, string>
            {
                ["Otlp__Endpoint"] = "http://collector.example:4318",
                ["Otlp__Protocol"] = "grpc",
            });

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains("Temporary password:", stdout);
        // Degraded, not silently exporting over plaintext.
        Assert.Contains("OTLP export disabled", stderr);
    }

    [Fact]
    public async Task RecoverAdmin_UnknownEmail_ExitsNonZeroWithClearMessage()
    {
        var (exitCode, _, stderr) = await RunAsync($"--email nobody-{Guid.NewGuid():N}@test.local");

        Assert.Equal(1, exitCode);
        Assert.Contains("Recovery failed", stderr);
    }

    // #450 — recover-admin is documented (AGENTS.md, #265) to run under the
    // app's least-privilege DML-only runtime role, never the higher-privileged
    // migrator credential — the whole point being that an operator never needs
    // to keep the elevated credential warm just for incident response. Every
    // OTHER test in this class connects with the Testcontainers superuser role,
    // which has DDL regardless of what the command needs, so none of them could
    // have caught #450 (an unconditional Database.MigrateAsync() call that
    // required CREATE on the schema just to read the migrations-history table,
    // verified live against production). This creates an ACTUAL restricted
    // Postgres role — the same USAGE + DML, no CREATE shape the #263 deploy
    // runbook describes for the runtime role — inside the same container, and
    // runs the command against it, so a future regression of the same shape
    // (some other verb picking up an unnecessary migrate/DDL call) fails here
    // instead of only surfacing against a real production database.
    [Fact]
    public async Task RecoverAdmin_UnderALeastPrivilegeDmlOnlyRole_StillRecovers()
    {
        var roleConnectionString = await CreateDmlOnlyRoleConnectionStringAsync();

        var (exitCode, stdout, stderr) = await RunAsync(
            $"--email {_factory.AdminEmail} --reason least-privilege-drill",
            extraEnvironment: new Dictionary<string, string>
            {
                ["ConnectionStrings__Default"] = roleConnectionString,
            });

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains("Temporary password:", stdout);
    }

    // Creates a Postgres role holding exactly the grants #263's deploy runbook
    // describes for the runtime role (USAGE on the schema, DML on every
    // existing table/sequence — explicitly NO CREATE), via the Testcontainers
    // superuser connection this factory already holds, and returns a
    // connection string for that role against the SAME database. The role
    // outlives this one call — cleaned up implicitly when the container is
    // disposed at the end of the fixture's lifetime, not worth a DROP ROLE
    // here.
    private async Task<string> CreateDmlOnlyRoleConnectionStringAsync()
    {
        var roleName = $"recover_test_role_{Guid.NewGuid():N}";
        const string rolePassword = "test-only-not-a-real-secret";

        await using (var admin = new NpgsqlConnection(_factory.ConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"""
                CREATE ROLE "{roleName}" LOGIN PASSWORD '{rolePassword}';
                GRANT USAGE ON SCHEMA public TO "{roleName}";
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "{roleName}";
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO "{roleName}";
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_factory.ConnectionString)
        {
            Username = roleName,
            Password = rolePassword,
        };
        return builder.ConnectionString;
    }

    private static string ExtractTemporaryPassword(string stdout)
    {
        const string marker = "Temporary password:";
        var line = stdout.Split('\n').First(l => l.Contains(marker));
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
