namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Reflection;
using Cluckwork.Api.Hosting;
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
// RateLimitingOptions.Validate, then `healthcheck` classified as a serving
// process. Every one was a registration-time throw for machinery only a SERVING
// process uses.
//
// ── The defect has TWO shapes, and one test catches only one of them ────────
//
// This was nearly shipped overstated. The first version of this file claimed all
// five instances "would have been caught here", and a mutation run refuted it:
// hoisting either config binding back outside its role-checked try kills the
// ProcessRoleGuardTests arms but leaves EVERY case here green. The reason is
// structural, not a gap to patch — a guard fires on one of two inputs:
//
//   ABSENT config  — #260's empty TrustedProxies, #319's missing AllowedHosts.
//                    Caught below by MinimalConfig, which supplies nothing.
//   MALFORMED config — a bad protocol, an unparseable CIDR, a non-numeric
//                    PermitLimit, a non-boolean AllowInsecureEndpoint. Minimal
//                    config is *well-formed by construction*, so binding and
//                    validation both succeed and the guard never runs.
//
// Four of the five instances are the malformed shape. So MinimalConfig alone
// would have caught one of them, and claiming otherwise made the weaker of the
// two tests read as the stronger. HostileServingOnlySection is the other half:
// one deliberately broken value per serving-only config section, isolated one
// section per case so no arm can hide behind another's throw.
//
// The rule the pair encodes: a verb gets a connection string and its own
// arguments, and nothing in a section it does not consume may stop it —
// `recover-admin` is the break-glass path for a farm that is already broken,
// which is exactly when the rest of the configuration is missing or wrong.
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

    private Process Start(string verbAndArgs, string environment) =>
        Start(verbAndArgs, environment, _ => { });

    private Process Start(string verbAndArgs, string environment, Action<ProcessStartInfo> configure)
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
        configure(psi);

        return Process.Start(psi)!;
    }

    public static TheoryData<string, string> EveryOneShotVerb() => new()
    {
        // Production on purpose: these run against real production databases.
        { "migrate", "Production" },
        { "recover-admin --email nobody@example.test", "Production" },
        { "bootstrap-admin --email owner@example.test", "Production" },
        // Read-only cross-account read (#531). Production on purpose: it is not
        // environment-gated and runs against the real database. An empty or
        // fully-populated table both exit 0; a missing/unreachable DB is its own
        // clean exit 1 (the verb's try/catch), never a crash.
        { "list-accounts", "Production" },
        // #534 — the lifecycle verbs. Production on purpose, and for the same
        // reason list-accounts is: neither is environment-gated, and both run
        // against a real database. An unknown farm code is the verb's OWN clean
        // exit 1, which these arms deliberately tolerate; what must never happen
        // is a crash out of service registration before the verb's code runs.
        { "suspend-account --slug no-such-farm", "Production" },
        { "reactivate-account --slug no-such-farm", "Production" },
        // Seeding is deliberately blocked in Production (#280), so BOTH sides of
        // that are cases. The Testing arm is the one that actually seeds.
        { "seed --profile demo", "Testing" },
        // ...and the Production arm, which must reach its own refusal. Testing
        // alone would bypass every Production-gated guard, so a new guard that
        // checks IsProduction() WITHOUT respecting ProcessRole would abort seed
        // before it ever got to say no, and every row here would stay green
        // (#347 review round 5, codex). A clean exit 1 is the assertion.
        { "seed --profile demo", "Production" },
        // Not an ICliCommand, and therefore the one verb ProcessRoles has to name
        // by hand — which is exactly how it came to be classified as a SERVING
        // process before this PR. Nothing here reads configuration (the verb
        // returns before the host is built), so its value is the regression, not
        // the config coverage.
        { "healthcheck", "Production" },
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

    // EveryOneShotVerb is hand-written, and a hand-written list is what this
    // whole suite exists to stop trusting: a new ICliCommand expands
    // ProcessRoles.OneShotVerbs automatically, so it would be classified
    // correctly in production and exercised by nothing here (#347 review round 5,
    // codex). Hold the cases against the real registry.
    [Fact]
    public void EveryDispatchedVerb_HasAMinimalConfigCase()
    {
        var tested = EveryOneShotVerb()
            .Select(row => ((string)row[0]).Split(' ')[0])
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = ProcessRoles.OneShotVerbs.Where(v => !tested.Contains(v)).ToArray();

        Assert.True(
            uncovered.Length == 0,
            $"verb(s) classified OneShot but never run here: {string.Join(", ", uncovered)}. "
            + "Add a case with the minimal configuration that verb genuinely needs — a verb no "
            + "arm executes is covered by nothing.");

        // The other direction: a case naming a verb the dispatcher no longer
        // classifies is testing a command that cannot be reached.
        var stale = tested.Where(v => !ProcessRoles.OneShotVerbs.Contains(v)).ToArray();
        Assert.True(
            stale.Length == 0,
            $"case(s) name verb(s) absent from ProcessRoles.OneShotVerbs: {string.Join(", ", stale)}.");
    }

    // ── The malformed half ────────────────────────────────────────────────────
    //
    // One broken value per serving-only section, each in its OWN case. Isolation
    // is the point: applying them together would let the first throw hide every
    // section behind it, which is the dead-disjunct defect ProcessRoleGuardTests
    // was rewritten twice to remove — the same mistake is available here.
    //
    // The value breaks the BINDING (a type mismatch), not a validation rule,
    // because binding is the outer of the two and the round that found bugs 3
    // and 4 found exactly that: a subsystem whose *validation* was correctly
    // role-scoped while its Get<T>() was not. A section that survives a value it
    // cannot even parse has its whole configuration inside the boundary.
    public static TheoryData<string, string, string> HostileServingOnlySections()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (section, key, value) in HostileValues)
            data.Add(section, key, value);
        return data;
    }

    // Keyed by the section's own SectionName constant so EverySection_IsCovered
    // can hold this against the real set rather than a remembered one.
    private static readonly (string Section, string EnvKey, string Value)[] HostileValues =
    [
        ("Otlp", "Otlp__AllowInsecureEndpoint", "not-a-bool"),
        ("RateLimiting", "RateLimiting__Login__PermitLimit", "not-a-number"),
        ("Idempotency", "Idempotency__LeaseDurationSeconds", "not-a-number"),
        ("FarmLogo", "FarmLogo__MaxUploadBytes", "not-a-number"),
        ("FarmBanner", "FarmBanner__MaxUploadBytes", "not-a-number"),
        ("Jwt", "Jwt__AccessTokenMinutes", "not-a-number"),
        // #543 — SharedState:Redis:ConnectionString is a string, so it cannot
        // fail BINDING; its serving-only failure mode is the malformed-value boot
        // guard (EnsureSharedStateConnectionValid). "abortConnect=false" parses
        // but names no endpoint, so a SERVING process rejects it — a one-shot
        // verb must instead degrade to the in-process fallback and survive.
        ("SharedState", "SharedState__Redis__ConnectionString", "abortConnect=false"),
    ];

    // Deliberate exclusions, with the reason each is NOT serving-only. A section
    // is excluded because a one-shot verb genuinely consumes it — never because
    // an arm was inconvenient.
    private static readonly Dictionary<string, string> BothRoleSections = new(StringComparer.Ordinal)
    {
        ["Database:Resilience"] =
            "every verb opens the same database, so its retry settings are the verb's own "
            + "configuration; this is the one section bound eagerly for BOTH roles on purpose",
        ["Simulation"] =
            "it is `seed --profile simulation`'s own input — a broken value there must fail that "
            + "verb, which is the opposite of what these arms assert",
    };

    [Theory]
    [MemberData(nameof(HostileServingOnlySections))]
    public async Task NoServingOnlySection_CanAbortAVerbByBeingUnparseable(
        string section, string envKey, string value)
    {
        // recover-admin, not migrate: it is the break-glass verb #331 actually
        // took out, it needs no schema, and its own failure here (no such user)
        // is a clean exit 1 that the assertions below deliberately tolerate.
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            Start("recover-admin --email nobody@example.test", "Production",
                psi => psi.Environment[envKey] = value),
            VerbTimeout,
            $"`recover-admin` did not exit with a broken {section} section.");

        Assert.False(
            stderr.Contains("Unhandled exception", StringComparison.Ordinal),
            $"a broken `{envKey}` aborted `recover-admin`. The {section} section is serving-only "
            + "machinery, so everything that can reject it — binding included — belongs inside a "
            + $"role-checked boundary. That is the #331 class. exit={exitCode}\nstderr={stderr}");

        Assert.True(
            exitCode is 0 or 1,
            $"`recover-admin` exited {exitCode} with a broken {section} section. "
            + $"stdout={stdout}\nstderr={stderr}");
    }

    // Walk every config section that exists and require a decision about it —
    // covered above, or excluded with a stated reason. AGENTS.md: prefer "walk
    // everything, exclude deliberately" over "list what I thought of". A new
    // options type added tomorrow fails here until someone classifies it.
    [Fact]
    public void EveryConfigSection_IsEitherProbedOrDeliberatelyExcluded()
    {
        var sections = new[] { typeof(Program).Assembly, typeof(Cluckwork.Infrastructure.Identity.JwtOptions).Assembly }
            .SelectMany(a => a.GetTypes())
            .Select(t => t.GetField("SectionName", BindingFlags.Public | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true, FieldType: { } ft } && ft == typeof(string))
            .Select(f => (string)f!.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(sections);

        var probed = HostileValues.Select(v => v.Section).ToHashSet(StringComparer.Ordinal);
        var unclassified = sections
            .Where(s => !probed.Contains(s) && !BothRoleSections.ContainsKey(s))
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            $"config section(s) with no decision: {string.Join(", ", unclassified)}. Either add a "
            + "hostile value proving a one-shot verb survives the section being unparseable, or "
            + "add it to BothRoleSections with the reason a verb genuinely consumes it.");

        // Both directions, so a renamed or deleted section cannot leave an entry
        // claiming coverage it no longer provides.
        var stale = probed.Concat(BothRoleSections.Keys)
            .Where(s => !sections.Contains(s, StringComparer.Ordinal))
            .ToArray();
        Assert.True(
            stale.Length == 0,
            $"these names match no SectionName constant: {string.Join(", ", stale)}. Renamed?");
    }
}
