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
// One Postgres for the whole class. The one-shot arms run `migrate` per guard —
// the first applies the schema, the rest converge on "already current" — so the
// alternative was a container per row for no added coverage, and every container
// is another chance to hit Docker flake on CI.
public sealed class ServingGuardDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public sealed class ProcessRoleGuardTests(ServingGuardDatabaseFixture database)
    : IClassFixture<ServingGuardDatabaseFixture>
{
    private readonly ServingGuardDatabaseFixture _database = database;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;

    // Arm 1 applies a full schema to a fresh database. The serving arms are
    // expected to die during boot, before any DB connection or Kestrel bind, so
    // they get a much shorter bound — a serving start that does NOT die is the
    // failure those arms look for, and waiting 120s to discover it is pure delay.
    private static readonly TimeSpan MigrateTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ServingBootTimeout = TimeSpan.FromSeconds(45);

    // The serving arms never reach a database — see the call site.
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=3;Command Timeout=3";

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
        new("#316 endpoint", "Otlp:Endpoint",
            Violate: psi =>
            {
                psi.Environment["Otlp__Endpoint"] = "http://collector.invalid:4317";
                // Must REMOVE, not merely not-set: the "#316 binding" row below
                // keys on the same setting and its Satisfy runs first, so leaving
                // it would acknowledge the plaintext endpoint and un-violate this
                // row. Second instance of that hazard in this table; the first is
                // the TrustedProxies pair.
                psi.Environment.Remove("Otlp__AllowInsecureEndpoint");
            },
            Satisfy: psi =>
            {
                psi.Environment["Otlp__Endpoint"] = "http://collector.invalid:4317";
                psi.Environment["Otlp__AllowInsecureEndpoint"] = "true";
            }),

        // A SECOND violation of the same subsystem, and its own row because it
        // had its own role behaviour: ParseProtocol sat OUTSIDE the role-checked
        // catch, so `Otlp:Protocol=bogus` killed `recover-admin` with SIGABRT 134
        // — #331 alive in a second form, three lines above its own fix, and the
        // #331 regression test stepped over it by setting a valid protocol
        // alongside the bad endpoint it was testing. One row per VIOLATION, not
        // per subsystem, is what makes that visible.
        new("#316 protocol", "Otlp:Protocol must be",
            Violate: psi => psi.Environment["Otlp__Protocol"] = "not-a-protocol",
            Satisfy: psi => psi.Environment["Otlp__Protocol"] = "grpc"),

        // A THIRD violation of the same subsystem: the config BINDING, which
        // throws from Get<OtlpOptions>() before any validator runs. Rows are per
        // violation and not per subsystem precisely because each of these three
        // was, at some point in this PR's review, outside the role boundary while
        // the others were inside it.
        // Token includes the binder's quoting (`at 'Otlp:…'`) on purpose: the
        // plain key name is NOT discriminating, because the #316 endpoint guard's
        // own message tells the operator to "set Otlp:AllowInsecureEndpoint=true".
        // The first attempt used the bare key and the endpoint arm went red on the
        // negative assertion — which is the assertion earning its place.
        new("#316 binding", "at 'Otlp:AllowInsecureEndpoint'",
            Violate: psi => psi.Environment["Otlp__AllowInsecureEndpoint"] = "not-a-boolean",
            Satisfy: psi => psi.Environment["Otlp__AllowInsecureEndpoint"] = "true"),

        // #260 — an empty trusted-proxy list silently makes HSTS inert and
        // collapses the per-IP login limiter to one global bucket.
        new("#260 empty", "RateLimiting:TrustedProxies is empty",
            // Removes rather than simply not-setting: the "#260 malformed" row
            // below keys on the SAME setting, so its Satisfy would otherwise
            // supply a valid value and quietly un-violate this row. Violations are
            // applied after every Satisfy for exactly this reason (see Start).
            Violate: psi => psi.Environment.Remove("RateLimiting__TrustedProxies__0"),
            Satisfy: psi => psi.Environment["RateLimiting__TrustedProxies__0"] = "10.0.0.0/8"),

        // The same key MALFORMED, which used to be classified the opposite way
        // from the same key being empty: empty was correctly serving-only, while
        // a bad CIDR threw from RateLimitingOptions.Validate at registration and
        // took out every verb.
        new("#260 malformed", "is not a valid CIDR network",
            Violate: psi => psi.Environment["RateLimiting__TrustedProxies__0"] = "not-a-cidr",
            Satisfy: psi => psi.Environment["RateLimiting__TrustedProxies__0"] = "10.0.0.0/8"),

        // The config BINDING for this section, which throws from Get<T>() before
        // any validator runs — the same third-violation shape as "#316 binding".
        new("rate-limit binding", "RateLimiting:Login:PermitLimit' to type",
            Violate: psi => psi.Environment["RateLimiting__Login__PermitLimit"] = "not-a-number",
            Satisfy: psi => psi.Environment["RateLimiting__Login__PermitLimit"] = "5"),

        // The rest of RateLimitingOptions.Validate, one row per CHECK rather than
        // one per helper: ValidateWindow and ValidateConcurrency each enforce two
        // different things, and a row per helper would leave one of each pair
        // authoritative for nothing.
        new("window permits", "RateLimiting:Login:PermitLimit must be greater than 0",
            Violate: psi => psi.Environment["RateLimiting__Login__PermitLimit"] = "0",
            Satisfy: psi => psi.Environment["RateLimiting__Login__PermitLimit"] = "5"),

        new("window seconds", "RateLimiting:Login:WindowSeconds must be greater than 0",
            Violate: psi => psi.Environment["RateLimiting__Login__WindowSeconds"] = "0",
            Satisfy: psi => psi.Environment["RateLimiting__Login__WindowSeconds"] = "60"),

        new("#311 permits", "RateLimiting:ReportsConcurrency:PermitLimit must be greater than 0",
            Violate: psi => psi.Environment["RateLimiting__ReportsConcurrency__PermitLimit"] = "0",
            Satisfy: psi => psi.Environment["RateLimiting__ReportsConcurrency__PermitLimit"] = "2"),

        new("#311 queue", "ReportsConcurrency:QueueLimit must be 0",
            Violate: psi => psi.Environment["RateLimiting__ReportsConcurrency__QueueLimit"] = "5",
            Satisfy: psi => psi.Environment["RateLimiting__ReportsConcurrency__QueueLimit"] = "0"),

        // #319 — a wildcard AllowedHosts turns Host-header filtering off, so a
        // forged Host is accepted.
        new("#319", "AllowedHosts is missing, blank, or wildcard",
            Violate: psi => psi.Environment["AllowedHosts"] = "*",
            Satisfy: psi => psi.Environment["AllowedHosts"] = "cluckwork-test.example"),

        // The two ValidateOnStart upload caps. Serving-only for a different
        // REASON than the three above — not a role check but a mechanism:
        // .ValidateOnStart() fires from Host.StartAsync, and CliDispatcher
        // operates on the BUILT host without ever starting it. They behave
        // correctly today and are rows precisely because nothing said so: convert
        // either to an eager check inside AddCluckworkFeatures — the shape #316
        // had — and it aborts every verb while the rest of this suite stays green.
        // The first derivation missed both because it walked `IsProduction` and
        // explicit `throw` sites, which is "list what I thought of" one
        // abstraction level up. ServingGuardCoverageTests now enumerates them.
        // #510, folded in here because its fix DEPENDS on this PR's mechanism:
        // making the JWT key check eager without a role scope would have been a
        // brand-new instance of the #331 class, in the most security-sensitive
        // file of the set. Four rows because there are four violations — each key
        // can be missing or unusable, and only one of the four ever failed the
        // boot before.
        //
        // Satisfy supplies a real generated pair (see Start); these rows break one
        // half of it at a time.
        new("#510 public missing", "Jwt:PublicKeyPem is not configured",
            Violate: psi => psi.Environment["Jwt__PublicKeyPem"] = "   ",
            Satisfy: psi => psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem),

        new("#510 public unusable", "Jwt:PublicKeyPem is not a usable PEM key",
            Violate: psi => psi.Environment["Jwt__PublicKeyPem"] =
                "-----BEGIN PUBLIC KEY-----\\nnot-base64\\n-----END PUBLIC KEY-----",
            Satisfy: psi => psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem),

        new("#510 private missing", "Jwt:PrivateKeyPem is not configured",
            Violate: psi => psi.Environment["Jwt__PrivateKeyPem"] = "   ",
            Satisfy: psi => psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem),

        new("#510 private unusable", "Jwt:PrivateKeyPem is not a usable PEM key",
            Violate: psi => psi.Environment["Jwt__PrivateKeyPem"] =
                "-----BEGIN PRIVATE KEY-----\\nnot-base64\\n-----END PRIVATE KEY-----",
            Satisfy: psi => psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem),

        // Each cap is TWO rows, not one. Both validators have a floor branch
        // (<= 0) and a distinct CEILING branch (> the domain constant), and the
        // first version violated only the floor — so deleting either ceiling
        // check left every arm green. That is the one-row-per-violation rule
        // broken inside the table that states it (#347 review round 5, codex).
        // The ceiling token carries the violated VALUE, so the two rows for one
        // setting cannot satisfy each other's negative assertion.
        new("FarmLogo floor", "FarmLogo:MaxUploadBytes must be greater than zero",
            Violate: psi => psi.Environment["FarmLogo__MaxUploadBytes"] = "0",
            Satisfy: psi => psi.Environment["FarmLogo__MaxUploadBytes"] = "2097152"),

        // ImageSanitizer.MaxByteLengthCeiling is 5 MiB — one byte over it.
        new("FarmLogo ceiling", "FarmLogo:MaxUploadBytes (5242881) cannot exceed",
            Violate: psi => psi.Environment["FarmLogo__MaxUploadBytes"] = "5242881",
            Satisfy: psi => psi.Environment["FarmLogo__MaxUploadBytes"] = "2097152"),

        new("FarmBanner floor", "FarmBanner:MaxUploadBytes must be greater than zero",
            Violate: psi => psi.Environment["FarmBanner__MaxUploadBytes"] = "0",
            Satisfy: psi => psi.Environment["FarmBanner__MaxUploadBytes"] = "5242880"),

        // ImageSanitizer.MaxBannerByteLengthCeiling is 15 MiB — one byte over it.
        new("FarmBanner ceiling", "FarmBanner:MaxUploadBytes (15728641) cannot exceed",
            Violate: psi => psi.Environment["FarmBanner__MaxUploadBytes"] = "15728641",
            Satisfy: psi => psi.Environment["FarmBanner__MaxUploadBytes"] = "5242880"),
    ];

    // Exposed so ServingGuardCoverageTests can hold this table against the source
    // it claims to enumerate — without that, deleting a row silently deletes an
    // arm and the suite stays green (#347 review round 3).
    internal static IReadOnlyList<string> CoveredGuardTokens =>
        [.. ServingOnlyGuards.Select(g => g.MessageToken)];

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
        string verb, string connectionString, ServingGuard violate)
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

        // Satisfy every OTHER guard first, then violate the target LAST. Two rows
        // key on the same setting (`RateLimiting:TrustedProxies` absent vs
        // malformed), so a Satisfy running afterwards would silently un-violate
        // the row under test — which is how the first version of this pair failed,
        // and it failed loudly, which is the point of the negative assertions.
        foreach (var guard in ServingOnlyGuards.Where(g => !ReferenceEquals(g, violate)))
            guard.Satisfy(psi);
        violate.Violate(psi);

        return Process.Start(psi)!;
    }

    // THE GUARANTEE, per guard. Violating any one of them must not abort the
    // one-shot verb — that is what #331 broke.
    //
    // Per guard rather than all-at-once, and that is not symmetry for its own
    // sake: two rows key on the same setting in mutually exclusive states
    // (`RateLimiting:TrustedProxies` absent vs malformed), so a single combined
    // run structurally CANNOT violate both, and whichever lost would have been
    // covered here by nothing. That is the same dead-disjunct shape one level
    // further out, and it is why the malformed case — a live bug this PR fixes —
    // needs its own run.
    [Theory]
    [MemberData(nameof(EveryServingGuard))]
    public async Task EachServingGuard_DoesNotAbortAOneShotVerb(string issue)
    {
        var guard = ServingOnlyGuards.Single(g => g.Issue == issue);

        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            Start("migrate", _database.ConnectionString, violate: guard), MigrateTimeout);

        Assert.True(
            0 == exitCode,
            $"the serving-only guard {issue} aborted the one-shot `migrate` verb — the #331 "
            + $"regression. expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
    }

    // The other direction, so the arms above are never vacuous for ANY guard.
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

        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            // No container here: every one of these guards stops the boot before
            // any database work (Production sets Database:MigrateOnStartup=false),
            // so an unreachable literal is enough — and it is not a weakening,
            // since a process that got far enough to need a database would die
            // without this arm's token and go red on the assertion below.
            Start(string.Empty, UnreachableDatabase, violate: guard),
            ServingBootTimeout,
            $"A serving boot that SUCCEEDS with {issue} violated means {issue} no longer fails a "
            + "Production boot, so the one-shot arm's coverage of it proves nothing.");

        Assert.True(
            exitCode != 0,
            $"{issue} alone did not fail a Production serving boot, so the one-shot arm does not "
            + $"actually exercise it. stdout={stdout} stderr={stderr}");

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
