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

    // #732 — the rename verb. Driven as a subprocess like every other verb, because the
    // contract under test is the exit code and the printed line, and only the process
    // that exits produces those.
    private Task<(int ExitCode, string Stdout, string Stderr)> RunRename(string current, string next,
        string extra = "") =>
        RunAsync($"rename-account --slug {current} --new-slug {next}{extra}");

    [Fact]
    public async Task RenameVerb_ChangesTheCode_PrintsOldAndNew_AndWritesOneAuditRow()
    {
        var email = $"rename-command-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = Slug(accountId);

        var (exitCode, stdout, stderr) = await RunRename(slug, "renamed-by-verb",
            " --reason \"rebrand drill\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains(slug, stdout);
        Assert.Contains("renamed-by-verb", stdout);
        Assert.Equal("renamed-by-verb", await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync()));

        var audit = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(a => a.AccountId == accountId && a.Action == "Account.Rename")
            .SingleAsync());
        Assert.Equal("rebrand drill", audit.Reason);
        Assert.Equal("(rename-account)", audit.ActorEmail);
        Assert.Equal(Guid.Empty, audit.ActorUserId);
    }

    // The current code is matched case-insensitively (an operator typing SECOND-FARM at a
    // shell means second-farm) while the NEW code is not folded — TryValidateSlug rejects
    // uppercase. Both directions in one test so the asymmetry cannot be "fixed" by
    // normalizing both.
    [Fact]
    public async Task RenameVerb_FoldsCaseForTheCurrentCode_ButRejectsAnUppercaseNewCode()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-case-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);

        var foldedTarget = "fold" + slug[^11..];
        var folded = await RunRename(slug.ToUpperInvariant(), foldedTarget);
        Assert.True(folded.ExitCode == 0, $"expected exit 0, got {folded.ExitCode}. stderr={folded.Stderr}");

        var uppercaseTarget = await RunRename(foldedTarget, "RENAMED-TWO");
        Assert.Equal(1, uppercaseTarget.ExitCode);
        Assert.Contains("Account.SlugInvalid", uppercaseTarget.Stderr);
    }

    [Fact]
    public async Task RenameVerb_RunTwice_ExitsZero_AndWritesNoSecondAuditRow()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-repeat-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);

        var target = "rep" + slug[^11..];
        var first = await RunRename(slug, target);
        var versionAfterFirst = await VersionAsync(accountId);
        var second = await RunRename(target, target);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("already", second.Stdout);
        Assert.Equal(versionAfterFirst, await VersionAsync(accountId));
        Assert.Equal(1, await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
    }

    [Fact]
    public async Task RenameVerb_ToAnotherFarmCode_ExitsOne_NamingSlugTaken_AndChangesNothing()
    {
        var first = await factory.SeedAccountWithUserAsync($"rename-taken-a-{Guid.NewGuid():N}@test.local");
        var second = await factory.SeedAccountWithUserAsync($"rename-taken-b-{Guid.NewGuid():N}@test.local");

        var result = await RunRename(Slug(second), Slug(first));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Account.SlugTaken", result.Stderr);
        Assert.Equal(Slug(second), await factory.WithTenantScopeAsync(second, db => db.Accounts
            .Where(a => a.Id == second).Select(a => a.Slug).SingleAsync()));
    }

    [Theory]
    [InlineData("api")]     // reserved: valid in shape, refused by the reserved branch
    [InlineData("ab")]      // too short
    [InlineData("SELF")]    // sentinel: swapped below for THIS farm's own code -> no-op
    public async Task RenameVerb_WithAnUnusableNewCode_BehavesPerContract(string candidate)
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-bad-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);
        if (candidate == "SELF") candidate = slug;

        var result = await RunRename(slug, candidate);

        if (candidate == slug)
        {
            // Renaming to the code it already has is a NO-OP, not a failure: an operator
            // retrying a half-remembered command must not see a red exit. No audit row,
            // no Version bump — pinned above and in the service tests.
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("already", result.Stdout);
            Assert.Equal(0, await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
                .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Account.SlugInvalid", result.Stderr);
        Assert.Equal(slug, await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync()));
    }

    // The sessions-survive acceptance criterion, asserted rather than reasoned about:
    // the refresh cookie and access token bind to the account id, so a rename must not
    // end anybody's session.
    [Fact]
    public async Task RenameVerb_LeavesAnExistingSessionWorking()
    {
        var email = $"rename-session-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = Slug(accountId);
        var tokens = await factory.LoginAsync(email);

        Assert.Equal(0, (await RunRename(slug, "live" + slug[^11..])).ExitCode);

        var response = await factory.CreateClient()
            .PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString());
        Assert.True(response.IsSuccessStatusCode,
            $"refresh after a rename must still work, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task RenameVerb_WithoutTheNewCode_ExitsOne_NamingTheFlag()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-flag-{Guid.NewGuid():N}@test.local");

        var result = await RunAsync($"rename-account --slug {Slug(accountId)}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--new-slug", result.Stderr);
    }

    [Fact]
    public async Task RenameVerb_WithAnUnknownCurrentCode_ExitsOne_NamingTheCode()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-unknown-{Guid.NewGuid():N}@test.local");
        var before = await VersionAsync(accountId);

        var result = await RunRename("missing-farm", "new" + Guid.NewGuid().ToString("N")[..11]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing-farm", result.Stderr);
        Assert.Equal(before, await VersionAsync(accountId));
    }

    [Fact]
    public async Task RenameVerb_InvalidCodeWithControlCharacters_StillWritesOneStderrLine()
    {
        var result = await RunRename("missing-farm", "\"bad\ncode\"");

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain('\n', result.Stderr.TrimEnd('\r', '\n'));
        Assert.Contains("'bad code' is not a valid farm code", result.Stderr);
    }
}
