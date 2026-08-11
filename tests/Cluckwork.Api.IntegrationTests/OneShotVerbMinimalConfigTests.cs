namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #347 — every one-shot verb must run under a Production environment containing
// ONLY the configuration that verb genuinely needs.
//
// This asserts the PROPERTY. ProcessRoleGuardTests enumerates the individual
// serving-only guards and pins which of them fails a serving boot; this pins the
// other half — that none of them, named or unnamed, can abort a verb — and it
// does so without anyone having to remember a table.
//
// It exists because enumerating was not working. Five instances of one bug class
// were found across four review rounds, each after the previous "scope that
// subsystem" fix: the #316 endpoint check (#331 itself), then ParseProtocol
// beside it, then the config BINDING beside that, then all of
// RateLimitingOptions.Validate, then the eager Jwt:PublicKeyPem requirement in a
// file no round had opened. Every one of them was a registration-time throw for
// machinery that only a SERVING process uses, and every one of them would have
// been caught here on the day it was written.
//
// So the rule this encodes is deliberately blunt: a verb gets a connection
// string and its own arguments. Anything else the process demands is a serving
// concern leaking into an operational escape hatch — and `recover-admin` is the
// break-glass path for a farm that is already broken, which is exactly when the
// rest of the configuration is missing.
public sealed class OneShotVerbMinimalConfigTests(ServingGuardDatabaseFixture database)
    : IClassFixture<ServingGuardDatabaseFixture>
{
    private readonly ServingGuardDatabaseFixture _database = database;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan VerbTimeout = TimeSpan.FromSeconds(120);

    // Only these are inherited. The child gets no ambient application config at
    // all, which is the whole point — an inherited Jwt__* or Otlp__* would hide
    // exactly the defect under test.
    private static readonly string[] InheritedOsVariables =
        ["PATH", "HOME", "DOTNET_ROOT", "TMPDIR", "LANG", "LC_ALL", "USER"];

    private Process Start(string verbAndArgs, string environment)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" {verbAndArgs}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.Environment.Clear();
        foreach (var name in InheritedOsVariables)
            if (Environment.GetEnvironmentVariable(name) is { } value)
                psi.Environment[name] = value;

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        // The database and the acknowledgement that the test container is
        // plaintext. The #261/#262 TLS floor applies to BOTH roles by design, so
        // opting out of it is not a serving concern leaking in.
        psi.Environment["ConnectionStrings__Default"] = _database.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";

        return Process.Start(psi)!;
    }

    public static TheoryData<string, string> EveryOneShotVerb() => new()
    {
        // Production on purpose: these run against real production databases.
        { "migrate", "Production" },
        { "recover-admin --email nobody@example.test", "Production" },
        { "bootstrap-admin --email owner@example.test", "Production" },
        // The seed profiles are deliberately blocked in Production (#280), so
        // their minimal environment is a non-Production one.
        { "seed --profile demo", "Testing" },
    };

    [Theory]
    [MemberData(nameof(EveryOneShotVerb))]
    public async Task EveryOneShotVerb_RunsWithOnlyTheConfigurationItNeeds(string verbAndArgs, string environment)
    {
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            Start(verbAndArgs, environment),
            VerbTimeout,
            $"`{verbAndArgs}` did not exit — a one-shot verb must never start the web host.");

        // The verb's OWN failures are fine and are covered by its own suite: an
        // unknown email is a clean exit 1 with a message. What must never happen
        // is a crash out of service registration, which is what every instance of
        // this bug class looked like — an unhandled InvalidOperationException,
        // exit 134, before the verb's code ran at all.
        Assert.False(
            stderr.Contains("Unhandled exception", StringComparison.Ordinal),
            $"`{verbAndArgs}` crashed instead of running. Some serving-only configuration is "
            + "required of a one-shot verb — that is the #331 class, and this is the fifth "
            + $"variant of it. exit={exitCode}\nstderr={stderr}");

        Assert.True(
            exitCode is 0 or 1,
            $"`{verbAndArgs}` exited {exitCode}; a one-shot verb exits 0 on success or 1 on its "
            + $"own clean failure, never a crash. stdout={stdout}\nstderr={stderr}");
    }
}
