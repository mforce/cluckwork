namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;
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

        var (exitCode, stdout, stderr) = await RunBootstrapCommandAsync(email);

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains(email, stdout);
        Assert.Contains("Temporary password:", stdout);
        // The password itself never reaches stderr (nothing does on success),
        // and — the actual "never the logger" guarantee — never a structured
        // log line either; that is asserted separately via SerilogSinkTests-
        // style coverage is out of scope here, but stderr staying empty on
        // success is the observable half from outside the process.
        Assert.Equal(string.Empty, stderr);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == email);
        Assert.Equal(SeedDefaults.AccountId, user.AccountId);
        Assert.True(user.MustChangePassword);
        Assert.True(await users.IsInRoleAsync(user, Roles.Owner));
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
}
