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
// for the serving process.
//
// ── Why the structure below is a TABLE and not a list of arms ──────────────
//
// Two earlier versions of this test had the same defect, which is what decided
// the shape. Both asserted "the boot died naming ONE OF these guards", and in
// both cases the guards run in a fixed order, so the later ones were never
// reached and their disjunct was dead:
//
//   v1: three guards, one disjunction. #316 is validated during SERVICE
//       REGISTRATION, ahead of ServingBootGuards, so the boot ALWAYS died there
//       and #260/#319 were never proven hostile at all.
//   v2: added an arm satisfying #316 — and repeated the mistake one level down.
//       EnsureServingConfiguration calls #260 then #319 unconditionally, so the
//       boot always died at #260 and the `AllowedHosts` disjunct was dead. A
//       mutant deleting the #319 call entirely survived every arm.
//
// AGENTS.md: two misses of the same shape mean the METHOD is wrong — prefer
// "walk everything, exclude deliberately" over "list what I thought of". So
// there is no list of arms here. Every serving-only guard is a row, and the
// suite derives one arm per row: violate exactly THAT guard, satisfy every
// other, and require the boot to die naming that guard AND NOT naming any
// other. Ordering between guards then cannot hide anything, because no arm ever
// has two violations to choose between. Adding a fourth guard is a row.
public sealed class ProcessRoleGuardTests
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;

    // Arm 1 applies a full schema to a fresh database. The serving arms are
    // expected to die during boot, before any DB connection or Kestrel bind, so
    // they get a much shorter bound — a serving start that does NOT die is the
    // failure those arms look for, and waiting 120s to discover it is pure delay.
    private static readonly TimeSpan MigrateTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ServingBootTimeout = TimeSpan.FromSeconds(45);

    // A Production SERVING boot must reject each of these, and none of them has
    // any bearing on what a one-shot verb does.
    //
    // Violate is what BREAKS the guard; Satisfy is the minimum that clears it.
    // For #260 the violation is the ABSENCE of configuration, which is exactly
    // why this suite builds the child environment from scratch (see Start): an
    // inherited value would silently satisfy it and no assertion would notice.
    private sealed record ServingGuard(
        string Issue,
        string MessageToken,
        Action<ProcessStartInfo> Violate,
        Action<ProcessStartInfo> Satisfy);

    private static readonly ServingGuard[] ServingOnlyGuards =
    [
        // #316 — plaintext OTLP in Production exposes telemetry and Otlp:Headers
        // credentials in transit. Satisfied by acknowledging the plaintext peer,
        // which keeps the endpoint set so the same code path still runs.
        new("#316", "Otlp:Endpoint",
            Violate: psi => psi.Environment["Otlp__Endpoint"] = "http://collector.invalid:4317",
            Satisfy: psi =>
            {
                psi.Environment["Otlp__Endpoint"] = "http://collector.invalid:4317";
                psi.Environment["Otlp__AllowInsecureEndpoint"] = "true";
            }),

        // #260 — an empty trusted-proxy list silently makes HSTS inert and
        // collapses the per-IP login limiter to one global bucket.
        new("#260", "RateLimiting:TrustedProxies",
            Violate: _ => { /* the ABSENCE of the key IS the violation */ },
            Satisfy: psi => psi.Environment["RateLimiting__TrustedProxies__0"] = "10.0.0.0/8"),

        // #319 — a wildcard AllowedHosts turns Host-header filtering off, so a
        // forged Host is accepted.
        new("#319", "AllowedHosts",
            Violate: psi => psi.Environment["AllowedHosts"] = "*",
            Satisfy: psi => psi.Environment["AllowedHosts"] = "cluckwork-test.example"),
    ];

    // Only these are inherited from the test host. Everything else the child sees
    // is set explicitly below.
    //
    // Built from scratch rather than by stripping known-bad prefixes, because
    // stripping is the same "list what I thought of" method that produced the
    // dead disjuncts above, and it had already sprung three leaks: the strip was
    // Ordinal while Linux env keys are case-sensitive and .NET config keys are
    // not (`ratelimiting__…` survived it), ASPNETCORE_-/DOTNET_-prefixed
    // variables bind to the same config keys with the prefix removed, and
    // `Otlp__` was never stripped at all — so an inherited
    // Otlp__AllowInsecureEndpoint (a documented sim-harness setting) would have
    // silently voided the #316 arm.
    private static readonly string[] InheritedOsVariables =
        ["PATH", "HOME", "DOTNET_ROOT", "TMPDIR", "LANG", "LC_ALL", "USER"];

    private static Process Start(
        string verb, string connectionString, ServingGuard? violate)
    {
        var arguments = verb.Length == 0 ? $"\"{ApiDllPath}\"" : $"\"{ApiDllPath}\" {verb}";
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.Environment.Clear();
        foreach (var name in InheritedOsVariables)
            if (Environment.GetEnvironmentVariable(name) is { } value)
                psi.Environment[name] = value;

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ConnectionStrings__Default"] = connectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        // The Testcontainers database is plaintext. The #261/#262 TLS floor
        // applies to BOTH roles by design, so it is opted out of rather than
        // being one of the guards under test — this suite is about role-scoped
        // guards, and the TLS floor deliberately isn't one.
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        // An ephemeral port, so a serving arm that wrongly BOOTS cannot collide
        // with anything else on the machine.
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

        // violate == null means "violate them all" (arm 1). Otherwise exactly one
        // guard is violated and every other is satisfied.
        foreach (var guard in ServingOnlyGuards)
        {
            if (violate is null || ReferenceEquals(guard, violate))
                guard.Violate(psi);
            else
                guard.Satisfy(psi);
        }

        return Process.Start(psi)!;
    }

    // ARM 1 — the guarantee. Every serving-only guard is violated at once and the
    // one-shot verb still runs to completion. This is what #331 broke, and each
    // guard is independently load-bearing here: misclassify any single one as
    // applying to OneShot and `migrate` aborts.
    [Fact]
    public async Task AllServingGuardsViolated_DoesNotAbortAOneShotVerb()
    {
        await using var db = new PostgreSqlBuilder("postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await db.StartAsync();

        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            Start("migrate", db.GetConnectionString(), violate: null), MigrateTimeout);

        Assert.True(
            0 == exitCode,
            "a serving-only boot guard aborted the one-shot `migrate` verb (the #331 regression). "
            + $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
    }

    // One arm per guard, so arm 1 above is never vacuous for ANY of them.
    //
    // Each case violates exactly one guard and satisfies the rest, then requires
    // the boot to die naming THAT guard and no other. The negative half is what
    // kills the defect two earlier versions of this test shipped: if the guards
    // were reordered, or one stopped firing and a different one caught the boot
    // instead, a "names one of them" assertion would stay green. This cannot.
    public static TheoryData<string> EveryServingGuard()
    {
        var data = new TheoryData<string>();
        foreach (var guard in ServingOnlyGuards)
            data.Add(guard.Issue);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryServingGuard))]
    public async Task EachServingGuard_AloneFailsAServingBoot(string issue)
    {
        var guard = ServingOnlyGuards.Single(g => g.Issue == issue);

        await using var db = new PostgreSqlBuilder("postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await db.StartAsync();

        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            Start(string.Empty, db.GetConnectionString(), violate: guard),
            ServingBootTimeout,
            $"A serving boot that SUCCEEDS with {issue} violated means {issue} no longer fails a "
            + "Production boot, so arm 1's coverage of it proves nothing.");

        Assert.True(
            exitCode != 0,
            $"{issue} alone did not fail a Production serving boot, so arm 1 does not actually "
            + $"exercise it. stdout={stdout} stderr={stderr}");

        Assert.True(
            stderr.Contains(guard.MessageToken, StringComparison.Ordinal),
            $"the serving boot failed with only {issue} violated, but the message does not name it "
            + $"('{guard.MessageToken}'). exit={exitCode} stderr={stderr}");

        // The half that makes this ordering-proof: no OTHER guard may be what
        // stopped the boot, because no other guard is violated.
        foreach (var other in ServingOnlyGuards.Where(g => !ReferenceEquals(g, guard)))
            Assert.False(
                stderr.Contains(other.MessageToken, StringComparison.Ordinal),
                $"only {issue} was violated, but the boot failed naming {other.Issue} "
                + $"('{other.MessageToken}') — that guard is firing when it should not, or "
                + $"{issue} is not what stopped the boot. exit={exitCode} stderr={stderr}");
    }
}
