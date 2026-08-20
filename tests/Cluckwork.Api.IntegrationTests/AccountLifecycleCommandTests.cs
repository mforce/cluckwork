namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
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

    private Task<int> LiveRefreshTokenCountAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens
            .CountAsync(token => token.AccountId == accountId && token.RevokedAt == null));

    [Fact]
    public async Task SuspendVerb_TakesTheFarmOffline_AndWritesOneAuditRowCarryingTheReasonAndSystemActor()
    {
        var email = $"suspend-command-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        _ = await factory.LoginAsync(email);
        var slug = Slug(accountId);

        var (exitCode, stdout, stderr) = await RunAsync(
            $"suspend-account --slug {slug} --reason \"non-payment drill\"");

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
        var second = await RunAsync($"suspend-account --slug {slug}");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("already suspended", second.Stdout);
        var count = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Suspend"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ReactivateVerb_BringsTheFarmBack_ButPreSuspensionRefreshTokensStayDead()
    {
        var email = $"reactivate-cycle-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        var slug = Slug(accountId);

        Assert.Equal(0, (await RunAsync($"suspend-account --slug {slug}")).ExitCode);
        var (exitCode, stdout, stderr) = await RunAsync($"reactivate-account --slug {slug}");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.True(await IsActiveAsync(accountId));
        var auditCount = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Reactivate"));
        Assert.Equal(1, auditCount);
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

    [Theory]
    [InlineData("suspend-account")]
    [InlineData("reactivate-account")]
    public async Task UnknownSlug_ExitsOne_AndChangesNothing(string command)
    {
        var accountId = await factory.SeedAccountWithUserAsync($"unknown-slug-{Guid.NewGuid():N}@test.local");
        const string unknownSlug = "missing-farm";

        var result = await RunAsync($"{command} --slug {unknownSlug}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(unknownSlug, result.Stderr);
        Assert.True(await IsActiveAsync(accountId));
        var auditCount = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId));
        Assert.Equal(0, auditCount);
    }

    [Fact]
    public async Task MissingSlugFlag_ExitsOne()
    {
        var result = await RunAsync("suspend-account");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--slug", result.Stderr);
    }
}
