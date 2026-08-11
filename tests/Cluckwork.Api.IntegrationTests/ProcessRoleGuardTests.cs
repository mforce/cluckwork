namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Testcontainers.PostgreSql;

// #347 — a SERVING-process boot guard must never abort a ONE-SHOT verb.
//
// This is the #331 shape, generalised. #331: the #316 OTLP endpoint validation
// ran at service registration, before CliDispatcher, so a plaintext endpoint
// killed `recover-admin` with SIGABRT 134 — the break-glass verb, the one that
// has to work when everything else is broken, taken out by a guard that exists
// for the serving process. The fix at the time was a `!IsCliInvocation(args)`
// bool threaded into registration; the fix here is a ProcessRole computed once
// and checked by each guard, so the answer to "does this guard fire for
// migrate?" is a property of the guard rather than of where its statement sits
// in Program.cs.
//
// The test is deliberately TWO-SIDED, because the one-sided version is worthless:
// asserting `migrate` exits 0 under a hostile serving configuration proves
// nothing unless that configuration genuinely WOULD fail a serving boot. So the
// second arm starts the same binary with the same environment and NO verb, and
// requires it to die naming a guard. Together: the config is serving-hostile,
// and the one-shot verb survives it.
//
// Each of the three settings below is independently load-bearing in the first
// arm — misclassify any single guard as applying to OneShot and `migrate`
// aborts, so this goes red without needing one subprocess per guard.
public sealed class ProcessRoleGuardTests
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;

    // The migrate arm applies a full schema to a fresh database; the serving arm
    // is expected to die during boot, long before it would bind Kestrel, so it
    // gets a much shorter bound — a serving start that DOESN'T die is the failure
    // this arm is looking for, and waiting 120s to discover that is pure delay.
    private static readonly TimeSpan MigrateTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ServingBootTimeout = TimeSpan.FromSeconds(45);

    // Configuration that a Production SERVING boot must reject on three separate,
    // independent guards:
    //   #260 RateLimiting:TrustedProxies empty  -> HSTS + the per-IP login limiter
    //                                              silently go inert.
    //   #319 AllowedHosts "*"                   -> Host-header filtering is off.
    //   #316 Otlp:Endpoint plaintext http in    -> telemetry and Otlp:Headers
    //        Production, no AllowInsecureEndpoint  credentials exposed in transit.
    // None of the three has any bearing on what a one-shot verb does.
    //
    // satisfyOtlp acknowledges the plaintext endpoint, so #316 passes and a
    // serving boot has to fail on #260/#319 instead. See arm 3 for why that
    // matters: #316 is validated during service registration, ahead of the other
    // two, so with all three violated the serving process ALWAYS dies at #316 and
    // the other two are never reached.
    private static void ApplyHostileServingConfiguration(ProcessStartInfo psi, bool satisfyOtlp)
    {
        // #260's condition is the ABSENCE of these keys, so an inherited value
        // from the test host or a CI runner would silently disarm it — and the
        // suite would stay green while proving less than it claims. Strip the
        // whole section rather than the two names known today.
        foreach (var inherited in psi.Environment.Keys
                     .Where(k => k.StartsWith("RateLimiting__", StringComparison.Ordinal))
                     .ToList())
            psi.Environment.Remove(inherited);

        psi.Environment["AllowedHosts"] = "*";
        psi.Environment["Otlp__Endpoint"] = "http://collector.invalid:4317";
        if (satisfyOtlp)
            psi.Environment["Otlp__AllowInsecureEndpoint"] = "true";
    }

    private static Process Start(string verb, string connectionString, bool satisfyOtlp = false)
    {
        var arguments = verb.Length == 0 ? $"\"{ApiDllPath}\"" : $"\"{ApiDllPath}\" {verb}";
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ConnectionStrings__Default"] = connectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        // The Testcontainers database is plaintext. The #261/#262 TLS floor applies
        // to BOTH roles by design, so it is opted out of here rather than being one
        // of the hostile settings under test — this suite is about role-scoped
        // guards, and the TLS floor deliberately isn't one.
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        // Port 0 = an ephemeral port, so the serving arm cannot collide with
        // anything else on the machine in the (failing) case where it boots.
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        ApplyHostileServingConfiguration(psi, satisfyOtlp);
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task ServingOnlyGuards_DoNotAbortAOneShotVerb_AndStillFailAServingBoot()
    {
        await using var db = new PostgreSqlBuilder("postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await db.StartAsync();
        var connectionString = db.GetConnectionString();

        // ARM 1 — the guarantee. The one-shot verb runs to completion under the
        // hostile serving configuration. This is what #331 broke.
        var (migrateExit, migrateOut, migrateErr) = await SeedCommandRunner.RunToCompletionAsync(
            Start("migrate", connectionString), MigrateTimeout);

        Assert.True(
            0 == migrateExit,
            "a serving-only boot guard aborted the one-shot `migrate` verb (the #331 regression). "
            + $"expected exit 0, got {migrateExit}. stdout={migrateOut} stderr={migrateErr}");

        // ARM 2 — proof that arm 1 was not vacuous. Same binary, same environment,
        // no verb: this is a serving start, and it must die. Asserting only "exit
        // non-zero" would accept a death from an unrelated cause (a missing key, an
        // unreachable database), so the message has to name one of the three
        // guards. Which one wins is NOT asserted: #316 throws during service
        // registration and the other two after Build(), and pinning that order here
        // would turn a legitimate future reordering into a red test for no reason.
        var (servingExit, servingOut, servingErr) = await SeedCommandRunner.RunToCompletionAsync(
            Start(string.Empty, connectionString),
            ServingBootTimeout,
            "a serving boot that SUCCEEDS here means this configuration is not hostile after all, "
            + "so arm 1 above proved nothing");

        Assert.True(
            servingExit != 0,
            "the configuration this test calls hostile did not actually fail a serving boot, so "
            + "arm 1 proved nothing. Re-check the #260/#316/#319 conditions. "
            + $"stdout={servingOut} stderr={servingErr}");

        var namesAGuard =
            servingErr.Contains("RateLimiting:TrustedProxies", StringComparison.Ordinal)
            || servingErr.Contains("AllowedHosts", StringComparison.Ordinal)
            || servingErr.Contains("Otlp:Endpoint", StringComparison.Ordinal);
        Assert.True(
            namesAGuard,
            "the serving boot failed, but not on any of the three guards this test configures — "
            + "so arm 1 is not exercising what it claims. "
            + $"exit={servingExit} stdout={servingOut} stderr={servingErr}");

        // ARM 3 — the half arm 2 structurally cannot cover. #316 is validated
        // during SERVICE REGISTRATION, ahead of ServingBootGuards, so with all
        // three settings violated a serving boot always dies at #316: arm 2 alone
        // proves only that ONE of the three is hostile, and #260/#319 could
        // quietly stop being violated (an appsettings default gaining a concrete
        // AllowedHosts, AllowNoTrustedProxies flipping true, a CI runner exporting
        // RateLimiting__*) with both arms still green and arm 1's coverage of them
        // silently vacuous. Acknowledge the plaintext endpoint so #316 passes, and
        // require the boot to die on one of the other two.
        var (pinnedExit, pinnedOut, pinnedErr) = await SeedCommandRunner.RunToCompletionAsync(
            Start(string.Empty, connectionString, satisfyOtlp: true),
            ServingBootTimeout,
            "a serving boot that SUCCEEDS here means #260 and #319 are no longer violated by this "
            + "configuration, so arm 1 no longer covers them");

        Assert.True(
            pinnedExit != 0,
            "with #316 satisfied, the serving boot survived — #260 and #319 are not actually "
            + $"violated by this configuration. stdout={pinnedOut} stderr={pinnedErr}");

        // Either may win; both are ServingBootGuards, and pinning which would make
        // a legitimate reordering red for no reason.
        Assert.True(
            pinnedErr.Contains("RateLimiting:TrustedProxies", StringComparison.Ordinal)
            || pinnedErr.Contains("AllowedHosts", StringComparison.Ordinal),
            "with #316 satisfied the serving boot failed, but on neither #260 nor #319 — so "
            + $"arm 1's coverage of those two is not established. exit={pinnedExit} stderr={pinnedErr}");
    }
}
