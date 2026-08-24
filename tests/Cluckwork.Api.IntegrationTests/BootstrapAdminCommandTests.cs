namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #283 — `dotnet Cluckwork.Api.dll bootstrap-admin --email <e>` end to end, as
// a real SUBPROCESS (same binary/entry point an operator runs), exactly like
// SeedCommandTests does for `seed --profile demo`: WebApplicationFactory never
// passes args, so the CLI dispatch branch is otherwise never covered.
//
// Own factory/container (own database): the command writes to the fixed
// SeedDefaults.AccountId, so it must not share a database with anything else
// that touches admin/Owner rows there.
public sealed class BootstrapAdminCommandTests : IClassFixture<CluckworkWebApplicationFactory>
{
    private readonly CluckworkWebApplicationFactory _factory;
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    public BootstrapAdminCommandTests(CluckworkWebApplicationFactory factory)
    {
        _factory = factory;
        // Forces host + Postgres container startup (schema migrated) before
        // any subprocess below depends on it.
        _ = _factory.Services;
    }

    private Process StartBootstrapCommand(string? email, string environment = "Testing", string? connectionString = null)
    {
        var arguments = email is null ? "bootstrap-admin" : $"bootstrap-admin --email {email}";
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__Default"] = connectionString ?? _factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return Process.Start(psi)!;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunBootstrapCommandAsync(
        string? email, string environment = "Testing", string? connectionString = null) =>
        SeedCommandRunner.RunToCompletionAsync(
            StartBootstrapCommand(email, environment, connectionString), SubprocessTimeout);

    [Fact]
    public async Task FirstRun_CreatesOwner_PrintsPasswordOnlyToStdout_AndSetsMustChangePassword()
    {
        var email = $"bootstrap-{Guid.NewGuid():N}@test.local";

        // #589 (codex P2) — the account slug is VARIED before the command runs
        // so the farm-code assertion discriminates. Every bootstrap test
        // database carries the same migration-seeded "default-farm", so reading
        // the slug back from the DB and asserting THAT constant would still pass
        // a regression that printed the literal "default-farm". Pinning the
        // varied value forces the command to actually read the account row.
        // Restored in `finally` because the tests in this class share `_factory`'s
        // one database.
        var variedSlug = "varied-farm-x7q2";
        var originalSlug = await OverrideDefaultAccountSlugAsync(variedSlug);
        try
        {
            var (exitCode, stdout, stderr) = await RunBootstrapCommandAsync(email);

            Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
            Assert.Contains(email, stdout);
            Assert.Contains("Temporary password:", stdout);
            // #589 — assert the FARM CODE is printed AND that it is the VARIED
            // value set above, not the seed "default-farm" (see the method
            // comment). #532 made the farm code a required login input, so
            // without this line the operator cannot sign in.
            Assert.Contains($"on farm {variedSlug} ", stdout);
            // The password itself never reaches stderr (nothing does on success).
            Assert.Equal(string.Empty, stderr);
            // #273 — the actual "never the logger" guarantee, asserted directly:
            // Serilog's own Console sink ALSO writes to this process's stdout (the
            // "Console" WriteTo in appsettings.json), so a stray structured log
            // line would land in this SAME captured stream, not go missing — this
            // is precisely how a regression here would be observable. NOT "exactly
            // 3 lines": a legitimate operational Serilog line (e.g. the connection-
            // string warning RecoverAdminCommandTests hits in Production) can
            // legally appear here too, so the invariant is narrower and precise —
            // the password appears EXACTLY ONCE in the whole capture (the one
            // explicit Console.Out line), and never inside a Serilog line (which
            // always opens with its outputTemplate's "[HH:mm:ss LVL]" bracket).
            var tempPassword = ExtractTemporaryPassword(stdout);
            Assert.Equal(1, CountOccurrences(stdout, tempPassword));
            Assert.DoesNotContain(
                stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => line.TrimStart().StartsWith('[') && line.Contains(tempPassword));
        }
        finally
        {
            // Restore is asserted (rows affected == 1) so the isolation the
            // finally provides is actually verified, not just claimed.
            Assert.Equal(1, await SetDefaultAccountSlugAsync(originalSlug));
        }

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == email);
        Assert.Equal(SeedDefaults.AccountId, user.AccountId);
        Assert.True(user.MustChangePassword);
        Assert.True(await users.IsInRoleAsync(user, Roles.Owner));

        // #500 — the User.Create row for the very first Owner. There is no
        // human to attribute it to (not even the user being created, who did
        // not exist when the verb started), so it declares the non-person it
        // is instead of the old "(unresolved)" fallback.
        var created = await db.AuditEvents.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == AuditActions.UserCreate && a.EntityId == user.Id);
        Assert.Equal(SystemActors.BootstrapAdmin, created.ActorEmail);
        Assert.Equal(Guid.Empty, created.ActorUserId);
    }

    [Fact]
    public async Task Rerun_AfterFirstRunSucceeded_IsIdempotent_PrintsNoSecret_AndExitsZero()
    {
        var email = $"bootstrap-rerun-{Guid.NewGuid():N}@test.local";
        var first = await RunBootstrapCommandAsync(email);
        Assert.True(0 == first.ExitCode, $"first run: expected exit 0, got {first.ExitCode}. stderr={first.Stderr}");

        // A second invocation — even with a DIFFERENT --email, since the
        // question is "does an Owner already exist", not "does this exact
        // email exist" — must be a clean no-op, never a second Owner and
        // never a second printed secret.
        var secondEmail = $"bootstrap-rerun2-{Guid.NewGuid():N}@test.local";
        var second = await RunBootstrapCommandAsync(secondEmail);

        Assert.True(0 == second.ExitCode, $"rerun: expected exit 0, got {second.ExitCode}. stderr={second.Stderr}");
        Assert.Contains("already provisioned", second.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Temporary password:", second.Stdout);
        Assert.DoesNotContain(secondEmail, second.Stdout);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owners = await users.GetUsersInRoleAsync(Roles.Owner);
        Assert.Single(owners, u => u.AccountId == SeedDefaults.AccountId);
    }

    [Fact]
    public async Task Rerun_WhenAlreadyProvisioned_DoesNotPrintTheFarmCode()
    {
        // Reuses the idempotent-rerun setup: first run provisions the Owner,
        // second run is a no-op where WasAlreadyProvisioned is true.
        var email = $"bootstrap-farmcode-{Guid.NewGuid():N}@test.local";
        // #589 (codex P2) — same varied-slug discipline as the first-run test:
        // the re-run bans the account's ACTUAL (varied) slug value, not one
        // phrasing of an "on farm" line and not the seed constant — a broken
        // re-run that emitted "Farm code: <slug>" under any label must red.
        // Restored in `finally` because the tests share `_factory`'s database.
        var variedSlug = "varied-farm-x7q2";
        var originalSlug = await OverrideDefaultAccountSlugAsync(variedSlug);
        try
        {
            var first = await RunBootstrapCommandAsync(email);
            Assert.True(0 == first.ExitCode, $"first run: expected exit 0, got {first.ExitCode}. stderr={first.Stderr}");

            var second = await RunBootstrapCommandAsync(email);
            Assert.True(0 == second.ExitCode, $"rerun: expected exit 0, got {second.ExitCode}. stderr={second.Stderr}");
            Assert.Contains("already provisioned", second.Stdout, StringComparison.OrdinalIgnoreCase);
            // #589 — the idempotent path must stay silent about the farm code: no
            // provisioning happened. TWO bans, because the value ban alone cannot
            // catch the real mutant. AlreadyProvisioned() returns Slug = null, so a
            // re-run branch that prints the slug prints an EMPTY string, which the
            // varied-value ban below can never see; the "on farm" phrase ban is what
            // catches that one-file mutant, while the value ban catches the stronger
            // two-file mutant that also populates the slug. (These two are what keep
            // the AlreadyProvisioned() branch honest.)
            Assert.DoesNotContain(variedSlug, second.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("on farm", second.Stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Restore is asserted (rows affected == 1) so the isolation the
            // finally provides is actually verified, not just claimed.
            Assert.Equal(1, await SetDefaultAccountSlugAsync(originalSlug));
        }
    }

    [Fact]
    public async Task MissingEmailFlag_ExitsNonZeroWithClearMessage()
    {
        var (exitCode, _, stderr) = await RunBootstrapCommandAsync(email: null);

        Assert.Equal(1, exitCode);
        Assert.Contains("--email", stderr);
    }

    // A freshly migrated database (this process's own MigrateAsync call
    // brings the schema current) that has NEVER had `bootstrap-admin` run
    // against it comes up with the #283 static reference data (account/
    // roles/grades) but genuinely no Owner — proving the "fresh migrated DB
    // is usable with no Seed:* config" guarantee doesn't silently depend on
    // this factory's shared container having already been bootstrapped by an
    // earlier test in the class.
    [Fact]
    public async Task AgainstAFreshlyMigratedDatabase_BaseReferenceDataAlreadyExists_NoBootstrapNeededForIt()
    {
        await using var freshDb = new Testcontainers.PostgreSql.PostgreSqlBuilder(
            "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await freshDb.StartAsync();

        var email = $"bootstrap-fresh-{Guid.NewGuid():N}@test.local";
        var (exitCode, stdout, stderr) = await RunBootstrapCommandAsync(
            email, connectionString: freshDb.GetConnectionString());

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains("Temporary password:", stdout);
    }

    // PR #339 review — the check-then-act race: two `bootstrap-admin`
    // invocations starting at once could both observe "no Owner yet" before
    // either commits and each mint a distinct Owner. Real, separate OS
    // processes (real separate Postgres connections) against the SAME fresh
    // database, launched together — the only way to exercise the actual
    // race a single-process in-memory test can't reproduce. Own throwaway
    // database: this must not share _factory's container, whose other tests
    // assert an exact Owner count.
    [Fact]
    public async Task ConcurrentInvocations_OnlyOneCreatesAnOwner_TheOtherIsACleanNoOp()
    {
        await using var freshDb = new Testcontainers.PostgreSql.PostgreSqlBuilder(
            "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await freshDb.StartAsync();
        var connectionString = freshDb.GetConnectionString();

        // Migrate ONCE, sequentially, before racing — the realistic scenario
        // (the dedicated `migrate` pre-deploy job, #263, already ran; two
        // operators/scripts then race `bootstrap-admin` itself). Concurrent
        // EF migration application against a never-migrated schema is a
        // separate, pre-existing concern this test isn't about — every real
        // deploy flow never runs bootstrap-admin concurrently with migrate.
        {
            var migrateOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>();
            new Cluckwork.Infrastructure.Providers.Postgres.PostgresDbContextConfigurator()
                .Configure(migrateOptions, connectionString, new Cluckwork.Infrastructure.Providers.DatabaseResilienceOptions());
            await using var migrateDb = new AppDbContext(migrateOptions.Options, new TenantContext());
            await migrateDb.Database.MigrateAsync();
        }

        var emailA = $"bootstrap-race-a-{Guid.NewGuid():N}@test.local";
        var emailB = $"bootstrap-race-b-{Guid.NewGuid():N}@test.local";

        // Started together (not awaited one at a time) so both subprocesses'
        // ProvisionAsync calls are genuinely in flight at once, both racing
        // to acquire the pg_advisory_lock.
        var taskA = RunBootstrapCommandAsync(emailA, connectionString: connectionString);
        var taskB = RunBootstrapCommandAsync(emailB, connectionString: connectionString);
        var results = await Task.WhenAll(taskA, taskB);

        foreach (var (exitCode, stdout, stderr) in results)
            Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");

        // Exactly one of the two actually created an Owner (printed a
        // secret); the other found one already there and no-opped. Never
        // both, never neither.
        var createdCount = results.Count(r => r.Stdout.Contains("Temporary password:"));
        var alreadyProvisionedCount = results.Count(
            r => r.Stdout.Contains("already provisioned", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, createdCount);
        Assert.Equal(1, alreadyProvisionedCount);

        // And the database itself agrees: exactly one Owner, under exactly
        // one of the two emails — never both, never a third.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>();
        new Cluckwork.Infrastructure.Providers.Postgres.PostgresDbContextConfigurator()
            .Configure(options, connectionString, new Cluckwork.Infrastructure.Providers.DatabaseResilienceOptions());
        await using var db = new AppDbContext(options.Options, new TenantContext());
        var owners = await db.Users.IgnoreQueryFilters()
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        var owner = Assert.Single(owners);
        Assert.Contains(owner.Email, new[] { emailA, emailB });
    }

    // #589 — set the default account's Slug to a value that is NOT the
    // migration-seeded "default-farm", and restore it. Raw SQL (not EF
    // SaveChanges) is deliberate: Slug is immutable in the domain (#531,
    // "no ChangeSlug"), and a raw UPDATE bypasses both the domain guard and any
    // interceptor — exactly what a test that fabricates a distinct farm code
    // needs. `Override` returns the original so a `finally` can restore it.
    private async Task<string> OverrideDefaultAccountSlugAsync(string newSlug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.Accounts.IgnoreQueryFilters()
            .Where(a => a.Id == SeedDefaults.AccountId)
            .Select(a => a.Slug)
            .SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Accounts\" SET \"Slug\" = {newSlug} WHERE \"Id\" = {SeedDefaults.AccountId}");
        return original;
    }

    private async Task<int> SetDefaultAccountSlugAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Accounts\" SET \"Slug\" = {slug} WHERE \"Id\" = {SeedDefaults.AccountId}");
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
