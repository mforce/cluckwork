namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[Collection(IntegrationCollection.Name)]
public sealed class AccountLifecycleCommandTests(CluckworkWebApplicationFactory factory)
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    private Process StartCommand(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ConnectionStrings__Default"] = factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        // The Testcontainers DB is plaintext; opt out of the #262 Production TLS
        // floor. The #260/#319 serving guards skip a one-shot verb (#347).
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return Process.Start(psi)!;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string arguments) =>
        SeedCommandRunner.RunToCompletionAsync(StartCommand(arguments), SubprocessTimeout);

    private static string Slug(Guid accountId) => "farm-" + accountId.ToString("N")[..12];

    private Task<bool> IsActiveAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db =>
            db.Accounts.Where(a => a.Id == accountId).Select(a => a.IsActive).SingleAsync());

    private Task<int> VersionAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db =>
            db.Accounts.Where(a => a.Id == accountId).Select(a => a.Version).SingleAsync());

    private Task<int> LiveRefreshTokenCountAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens
            .CountAsync(token => token.AccountId == accountId && token.RevokedAt == null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendVerb_TakesTheFarmOffline_AndWritesOneAuditRowCarryingTheReasonAndSystemActor(
        bool uppercaseSlug)
    {
        var email = $"suspend-command-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _ = await factory.LoginAsync(email);
        var slug = Slug(accountId);
        var commandSlug = uppercaseSlug ? slug.ToUpperInvariant() : slug;

        var (exitCode, stdout, stderr) = await RunAsync(
            $"suspend-account --slug {commandSlug} --reason \"non-payment drill\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains(slug, stdout);
        Assert.False(await IsActiveAsync(accountId));
        var audit = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(a => a.AccountId == accountId && a.Action == "Account.Suspend")
            .SingleAsync());
        Assert.Equal("Account", audit.EntityType);
        Assert.Equal(accountId, audit.EntityId);
        Assert.Equal("non-payment drill", audit.Reason);
        Assert.Equal("(suspend-account)", audit.ActorEmail);
        Assert.Equal(Guid.Empty, audit.ActorUserId);
    }

    [Fact]
    public async Task SuspendVerb_RunTwice_ExitsZero_AndWritesNoSecondAuditRow()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"suspend-repeat-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);

        var first = await RunAsync($"suspend-account --slug {slug}");
        var versionAfterFirstSuspend = await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => a.Version)
            .SingleAsync());
        var second = await RunAsync($"suspend-account --slug {slug}");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("already suspended", second.Stdout);
        var count = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Suspend"));
        Assert.Equal(1, count);
        var versionAfterSecondSuspend = await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => a.Version)
            .SingleAsync());
        Assert.Equal(versionAfterFirstSuspend, versionAfterSecondSuspend);
    }

    [Fact]
    public async Task SuspendVerb_RunAgainstAnAlreadySuspendedFarm_StillRevokesALiveSession()
    {
        var email = $"suspend-rerevoke-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = Slug(accountId);

        Assert.Equal(0, (await RunAsync($"suspend-account --slug {slug}")).ExitCode);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            var epoch = await db.Users.Where(u => u.Id == userId).Select(u => u.CredentialEpoch).SingleAsync();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                TokenHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IssuedEpoch = epoch,
            });
            await db.SaveChangesAsync();
        });
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));

        Assert.Equal(0, (await RunAsync($"suspend-account --slug {slug}")).ExitCode);
        Assert.Equal(0, await LiveRefreshTokenCountAsync(accountId));
        var auditCount = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Suspend"));
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task ReactivateVerb_BringsTheFarmBack_ButPreSuspensionRefreshTokensStayDead()
    {
        var email = $"reactivate-cycle-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        var slug = Slug(accountId);

        Assert.Equal(0, (await RunAsync($"suspend-account --slug {slug}")).ExitCode);
        var (exitCode, stdout, stderr) = await RunAsync(
            $"reactivate-account --slug {slug} --reason \"paid up\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.True(await IsActiveAsync(accountId));
        var audit = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(a => a.AccountId == accountId && a.Action == "Account.Reactivate")
            .SingleAsync());
        Assert.Equal("paid up", audit.Reason);
        Assert.Equal("(reactivate-account)", audit.ActorEmail);
        Assert.Equal(Guid.Empty, audit.ActorUserId);
        var response = await factory.CreateClient().PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Identity.InvalidRefreshToken", problem!.Title);
    }

    [Fact]
    public async Task ReactivateVerb_OnAnAlreadyActiveFarm_ChangesNothing_AndRevokesNoSession()
    {
        var email = $"reactivate-noop-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _ = await factory.LoginAsync(email);
        var before = await LiveRefreshTokenCountAsync(accountId);

        var result = await RunAsync($"reactivate-account --slug {Slug(accountId)}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(before, await LiveRefreshTokenCountAsync(accountId));
        var auditCount = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Reactivate"));
        Assert.Equal(0, auditCount);
    }

    [Fact]
    public async Task ReactivateVerb_OnAnAlreadyActiveFarm_DoesNotAdvanceTheFarmSettingsVersion()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"reactivate-version-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);
        var versionBefore = await VersionAsync(accountId);

        Assert.Equal(0, (await RunAsync($"reactivate-account --slug {slug}")).ExitCode);
        Assert.Equal(versionBefore, await VersionAsync(accountId));

        Assert.Equal(0, (await RunAsync($"suspend-account --slug {slug}")).ExitCode);
        Assert.True(await VersionAsync(accountId) > versionBefore);
    }

    [Theory]
    [InlineData("suspend-account")]
    [InlineData("reactivate-account")]
    public async Task UnknownSlug_ExitsOne_AndChangesNothing(string command)
    {
        var accountId = await factory.SeedAccountWithUserAsync($"unknown-slug-{Guid.NewGuid():N}@test.local");
        const string unknownSlug = "missing-farm";
        var before = await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => new { a.Version, a.IsActive })
            .SingleAsync());

        var result = await RunAsync($"{command} --slug {unknownSlug}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(unknownSlug, result.Stderr);
        var after = await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => new { a.Version, a.IsActive })
            .SingleAsync());
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.IsActive, after.IsActive);
    }

    [Theory]
    [InlineData("suspend-account")]
    [InlineData("reactivate-account")]
    public async Task MissingSlugFlag_ExitsOne(string command)
    {
        var result = await RunAsync(command);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--slug", result.Stderr);
    }
}
