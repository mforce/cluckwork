namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.Extensions.DependencyInjection;

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

    private Process StartRecoverCommand(string arguments, string environment)
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
        return Process.Start(psi)!;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string arguments, string environment = "Production") =>
        SeedCommandRunner.RunToCompletionAsync(
            StartRecoverCommand(arguments, environment), SubprocessTimeout);

    [Fact]
    public async Task RecoverAdmin_InProduction_ResetsPassword_PrintsWorkingTemporaryPassword()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            $"--email {_factory.AdminEmail} --reason integration-drill");

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains("Temporary password:", stdout);

        // The printed one-time password must actually work, and the old one must not.
        var tempPassword = ExtractTemporaryPassword(stdout);
        using var scope = _factory.Services.CreateScope();
        var idp = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        Assert.True((await idp.LoginAsync(_factory.AdminEmail, tempPassword)).IsSuccess,
            "the printed temporary password should log in");
        Assert.True((await idp.LoginAsync(_factory.AdminEmail, _factory.AdminPassword)).IsFailure,
            "the old seeded password must be rejected after recovery");
    }

    [Fact]
    public async Task RecoverAdmin_UnknownEmail_ExitsNonZeroWithClearMessage()
    {
        var (exitCode, _, stderr) = await RunAsync($"--email nobody-{Guid.NewGuid():N}@test.local");

        Assert.Equal(1, exitCode);
        Assert.Contains("Recovery failed", stderr);
    }

    private static string ExtractTemporaryPassword(string stdout)
    {
        const string marker = "Temporary password:";
        var line = stdout.Split('\n').First(l => l.Contains(marker));
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }
}
