namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection(IntegrationCollection.Name)]
public sealed class AccountProvisioningTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task Provision_CreatesAFarmWithReferenceDataAndAnOwner()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"provision-{suffix}";
        var email = $"owner-{suffix}@example.test";

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("  Second Farm  ", slug, email, " UTC ", "en-US", "usd");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : string.Empty);
        var outcome = result.Value;
        Assert.Equal(slug, outcome.Slug);

        using var checkScope = factory.Services.CreateScope();
        var db = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == outcome.AccountId);
        Assert.Equal("Second Farm", account.Name);
        Assert.Equal("UTC", account.TimeZoneId);
        Assert.Equal("USD", account.DefaultCurrencyCode);
        var grades = await db.EggGrades.IgnoreQueryFilters()
            .Where(grade => grade.AccountId == outcome.AccountId)
            .ToListAsync();
        ReferenceDataComparison.AssertMappedPropertiesEqualByKey(
            db.Model.FindEntityType(typeof(EggGrade))!,
            grades,
            EggGrade.Defaults(outcome.AccountId, SeedDefaults.FarmId),
            grade => grade.Name,
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(EggGrade.Id), nameof(EggGrade.AccountId), nameof(EggGrade.Version),
            });
        var conversions = await db.EggUnitConversions.IgnoreQueryFilters()
            .Where(conversion => conversion.AccountId == outcome.AccountId)
            .ToListAsync();
        ReferenceDataComparison.AssertMappedPropertiesEqualByKey(
            db.Model.FindEntityType(typeof(EggUnitConversion))!,
            conversions,
            EggUnitConversion.Defaults(outcome.AccountId),
            conversion => conversion.UnitCode,
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(EggUnitConversion.Id),
                nameof(EggUnitConversion.AccountId),
                nameof(EggUnitConversion.Version),
            });

        var owner = await db.Users.SingleAsync(u => u.AccountId == outcome.AccountId && u.Email == email);
        Assert.Null(owner.DisabledAt);
        Assert.True(owner.MustChangePassword);
        Assert.Equal(1, owner.CredentialEpoch);
        var accountOwners = await checkScope.ServiceProvider.GetRequiredService<IAccountUserDirectory>()
            .FindByAccountRoleAsync(outcome.AccountId, Roles.Owner);
        Assert.Equal(owner.Id, Assert.Single(accountOwners).Id);

        var actions = await db.AuditEvents.IgnoreQueryFilters()
            .Where(a => a.AccountId == outcome.AccountId)
            .OrderBy(a => a.Action)
            .Select(a => new { a.Action, a.ActorEmail, a.AccountId })
            .ToListAsync();
        Assert.Equal([AuditActions.AccountProvisioned, AuditActions.UserCreate], actions.Select(a => a.Action));
        Assert.All(actions, action =>
        {
            Assert.Equal(SystemActors.ProvisionAccount, action.ActorEmail);
            Assert.Equal(outcome.AccountId, action.AccountId);
        });

        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(email, outcome.TemporaryPassword)).StatusCode);
    }

    [Theory]
    [InlineData("name-required", "Provision.NameRequired")]
    [InlineData("name-too-long", "Provision.NameTooLong")]
    [InlineData("email-required", "Provision.EmailRequired")]
    [InlineData("slug-invalid", "Account.SlugInvalid")]
    [InlineData("slug-reserved", "Account.SlugInvalid")]
    [InlineData("timezone-invalid", "Provision.TimeZoneUnknown")]
    [InlineData("timezone-windows-id", "Provision.TimeZoneUnknown")]
    [InlineData("locale-invalid", "Provision.LocaleInvalid")]
    [InlineData("currency-invalid", "Provision.CurrencyInvalid")]
    public async Task Provision_RejectsInvalidInputWithoutWrites(string invalidField, string expectedCode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var name = invalidField == "name-required" ? " "
            : invalidField == "name-too-long" ? new string('x', Account.MaxNameLength + 1)
            : "Valid Farm";
        var slug = invalidField == "slug-invalid" ? "UPPER-FARM"
            : invalidField == "slug-reserved" ? "admin"
            : $"valid-{suffix}";
        var email = invalidField == "email-required" ? " " : $"owner-{suffix}@example.test";
        var timeZone = invalidField == "timezone-invalid" ? "Nowhere/Invalid"
            : invalidField == "timezone-windows-id" ? "Pacific Standard Time"
            : "UTC";
        var locale = invalidField == "locale-invalid" ? "zz-ZZ" : "en-US";
        var currency = invalidField == "currency-invalid" ? "US" : "USD";
        var countsBefore = await WriteCountsAsync();

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync(name, slug, email, timeZone, locale, currency);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(countsBefore, await WriteCountsAsync());
    }

    [Fact]
    public async Task Provision_WhenIdentityRejectsOwner_RollsBackTheAccount()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"identity-{suffix}";

        var countsBefore = await WriteCountsAsync();
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Identity Failure", slug, "not-an-email");

        Assert.True(result.IsFailure);
        Assert.Equal("Users.CreateFailed", result.Error.Code);
        Assert.Equal(countsBefore, await WriteCountsAsync());
    }

    [Fact]
    public async Task Provision_WhenCommittedSlugAndOwnerMatch_ReturnsRecoverableCommand()
    {
        var email = $"recoverable-{Guid.NewGuid():N}@example.test";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = await SlugForAsync(accountId);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Ignored", slug, email);

        Assert.True(result.IsFailure);
        Assert.Equal("Provision.SlugTakenRecoverable", result.Error.Code);
        Assert.Contains($"recover-admin --email {email} --account {accountId}", result.Error.Description);
    }

    [Fact]
    public async Task Provision_WhenFarmIsSuspendedAndOwnerEnabled_ReturnsReactivationAdvice()
    {
        var email = $"suspended-enabled-{Guid.NewGuid():N}@example.test";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = await SlugForAsync(accountId);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.Suspend();
            await db.SaveChangesAsync();
        });

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Ignored", slug, email);

        Assert.True(result.IsFailure);
        Assert.Equal("Provision.SlugTakenSuspended", result.Error.Code);
        Assert.Contains("reactivate-account", result.Error.Description);
    }

    [Fact]
    public async Task Provision_WhenFarmIsSuspendedAndOwnerDisabled_PrioritizesReactivation()
    {
        var email = $"suspended-{Guid.NewGuid():N}@example.test";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = await SlugForAsync(accountId);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.Suspend();
            await db.Users.Where(u => u.AccountId == accountId && u.Email == email)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    user => user.DisabledAt, new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
        });

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Ignored", slug, email);

        Assert.True(result.IsFailure);
        Assert.Equal("Provision.SlugTakenSuspended", result.Error.Code);
        Assert.Contains("reactivate-account", result.Error.Description);
    }

    [Fact]
    public async Task Provision_WhenMatchingOwnerIsDisabled_ReturnsDisabledWithoutDeadEndRecoveryAdvice()
    {
        var email = $"disabled-{Guid.NewGuid():N}@example.test";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = await SlugForAsync(accountId);
        await factory.WithTenantScopeAsync(accountId, db => db.Users
            .Where(user => user.AccountId == accountId && user.Email == email)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                user => user.DisabledAt, new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero))));

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Ignored", slug, email);

        Assert.True(result.IsFailure);
        Assert.Equal("Provision.SlugTakenOwnerDisabled", result.Error.Code);
        Assert.DoesNotContain("recover-admin", result.Error.Description);
    }

    [Fact]
    public async Task Provision_WhenSlugBelongsToDifferentEmail_ReturnsPlainSlugTaken()
    {
        var accountId = await factory.SeedAccountWithUserAsync(
            $"actual-{Guid.NewGuid():N}@example.test");
        var slug = await SlugForAsync(accountId);
        var countsBefore = await WriteCountsAsync();

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Ignored", slug, $"different-{Guid.NewGuid():N}@example.test");

        Assert.True(result.IsFailure);
        Assert.Equal("Provision.SlugTaken", result.Error.Code);
        Assert.DoesNotContain("recover-admin", result.Error.Description);
        Assert.Equal(countsBefore, await WriteCountsAsync());
    }

    [Fact]
    public async Task Provision_WhenMatchingUserIsNotAnOwner_ReturnsPlainSlugTaken()
    {
        var ownerEmail = $"owner-{Guid.NewGuid():N}@example.test";
        var workerEmail = $"worker-{Guid.NewGuid():N}@example.test";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var slug = await SlugForAsync(accountId);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Ignored", slug, workerEmail);

        Assert.True(result.IsFailure);
        Assert.Equal("Provision.SlugTaken", result.Error.Code);
        Assert.DoesNotContain("recover-admin", result.Error.Description);
    }

    [Fact]
    public async Task Provision_WithMultipleOwners_FindsTheMatchingOwnerBeyondTheFirstId()
    {
        var firstEmail = $"owner-a-{Guid.NewGuid():N}@example.test";
        var secondEmail = $"owner-b-{Guid.NewGuid():N}@example.test";
        var accountId = await factory.SeedAccountWithUserAsync(firstEmail);
        await factory.SeedUserAsync(accountId, secondEmail, Roles.Owner);
        var owners = await OwnerIdsAndEmailsAsync(accountId);
        var matching = owners.OrderBy(owner => owner.Id).Last();
        var slug = await SlugForAsync(accountId);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Ignored", slug, matching.Email);

        Assert.True(result.IsFailure);
        Assert.Equal("Provision.SlugTakenRecoverable", result.Error.Code);
        Assert.Contains($"--email {matching.Email} --account {accountId}", result.Error.Description);
    }

    [Fact]
    public void IsSlugConflict_RejectsAnUnrelatedUniqueConstraint()
    {
        var postgres = new Npgsql.PostgresException(
            "duplicate", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation,
            "detail", "hint", 0, 0, "query", "where", "public", "AspNetRoles",
            "NormalizedName", "text", "RoleNameIndex", "file", "1", "routine");

        Assert.False(AccountProvisioner.IsSlugConflict(new DbUpdateException("failed", postgres)));
    }

    [Fact]
    public async Task ConcurrentProvisioning_UsesTheSlugIndexAsTheAuthority()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"race-{suffix}";
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var first = ProvisionWithoutPrecheckAsync(firstId, slug, $"first-{suffix}@example.test");
        var second = ProvisionWithoutPrecheckAsync(secondId, slug, $"second-{suffix}@example.test");
        var results = await Task.WhenAll(first, second);

        var winner = Assert.Single(results, result => result.IsSuccess).Value;
        var loser = Assert.Single(results, result => result.IsFailure);
        Assert.Equal("Provision.SlugTaken", loser.Error.Code);
        Assert.True(winner.AccountId == firstId || winner.AccountId == secondId);
        var loserId = winner.AccountId == firstId ? secondId : firstId;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Accounts.IgnoreQueryFilters().CountAsync(a => a.Slug == slug));
        Assert.Equal(10, await db.EggGrades.IgnoreQueryFilters()
            .CountAsync(grade => grade.AccountId == winner.AccountId));
        Assert.Equal(6, await db.EggUnitConversions.IgnoreQueryFilters()
            .CountAsync(conversion => conversion.AccountId == winner.AccountId));
        Assert.Equal(1, await db.Users.CountAsync(user => user.AccountId == winner.AccountId));
        Assert.Equal(2, await db.AuditEvents.IgnoreQueryFilters()
            .CountAsync(audit => audit.AccountId == winner.AccountId));

        Assert.Equal(0, await db.Accounts.IgnoreQueryFilters().CountAsync(a => a.Id == loserId));
        Assert.Equal(0, await db.EggGrades.IgnoreQueryFilters().CountAsync(g => g.AccountId == loserId));
        Assert.Equal(0, await db.EggUnitConversions.IgnoreQueryFilters()
            .CountAsync(c => c.AccountId == loserId));
        Assert.Equal(0, await db.Users.CountAsync(u => u.AccountId == loserId));
        Assert.Equal(0, await db.AuditEvents.IgnoreQueryFilters().CountAsync(a => a.AccountId == loserId));
    }

    private async Task<Result<AccountProvisionOutcome>> ProvisionWithoutPrecheckAsync(
        Guid accountId, string slug, string email)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionSkippingSlugPrecheckForTestAsync(
                accountId, "Race Farm", slug, email, "UTC", "en-US", "USD");
    }

    private async Task<WriteCounts> WriteCountsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new WriteCounts(
            await db.Accounts.IgnoreQueryFilters().CountAsync(),
            await db.EggGrades.IgnoreQueryFilters().CountAsync(),
            await db.EggUnitConversions.IgnoreQueryFilters().CountAsync(),
            await db.Users.CountAsync(),
            await db.AuditEvents.IgnoreQueryFilters().CountAsync());
    }

    private async Task<IReadOnlyList<(Guid Id, string Email)>> OwnerIdsAndEmailsAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IAccountUserDirectory>();
        var owners = await directory.FindByAccountRoleAsync(accountId, Roles.Owner);
        return owners.Select(owner => (owner.Id, owner.Email!)).ToArray();
    }

    private async Task<string> SlugForAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Accounts.IgnoreQueryFilters()
            .Where(a => a.Id == accountId)
            .Select(a => a.Slug)
            .SingleAsync();
    }

    private sealed record WriteCounts(
        int Accounts, int Grades, int Conversions, int Users, int AuditEvents);
}
