namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #732 — service contract below the CLI. The stale-source test is a REAL two-writer
// schedule: a transaction holds the source row, the service resolves the old slug and
// queues on FOR UPDATE, then the holder renames and commits. The service must inspect the
// freshly locked row and refuse rather than overwrite that committed rename.
[Collection(IntegrationCollection.Name)]
public sealed class AccountRenameServiceTests(CluckworkWebApplicationFactory factory)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}@test.local";
    private static string Slug(Guid accountId) => "farm-" + accountId.ToString("N")[..12];
    private static string Target(string prefix, Guid accountId) => prefix + accountId.ToString("N")[..11];

    private async Task<RenameOutcome> RenameAsync(string current, string next)
    {
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountRenameService>()
            .RenameAsync(current, next, "drill", CancellationToken.None);
        return new RenameOutcome(result.IsSuccess, result.IsFailure ? result.Error.Code : null,
            result.IsSuccess ? result.Value.Changed : null);
    }

    private sealed record RenameOutcome(bool Success, string? ErrorCode, bool? Changed);

    private Task<string> SlugAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync());

    private Task<int> VersionAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Version).SingleAsync());

    private async Task<(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, int Pid)>
        FenceAccountAsync(Guid accountId)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        return (db, tx, await db.BackendPidAsync());
    }

    [Fact]
    public async Task Rename_ChangesTheCode_BumpsVersion_AndWritesOneAuditRow()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("rename-ok"));
        var current = Slug(accountId);
        var target = Target("ok", accountId);
        var versionBefore = await VersionAsync(accountId);

        var outcome = await RenameAsync(current, target);

        Assert.True(outcome.Success, $"expected success, got {outcome.ErrorCode}");
        Assert.True(outcome.Changed);
        Assert.Equal(target, await SlugAsync(accountId));
        Assert.Equal(versionBefore + 1, await VersionAsync(accountId));
        var audit = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(a => a.AccountId == accountId && a.Action == "Account.Rename").SingleAsync());
        Assert.Equal("Account", audit.EntityType);
        Assert.Equal(accountId, audit.EntityId);
        Assert.Equal("drill", audit.Reason);
        Assert.Equal(SystemActors.RenameAccount, audit.ActorEmail);
        Assert.Equal(Guid.Empty, audit.ActorUserId);
        Assert.Contains("\"from\":\"" + current + "\"", audit.DetailsJson);
        Assert.Contains("\"to\":\"" + target + "\"", audit.DetailsJson);
    }

    [Fact]
    public async Task Rename_WhenSourceChangesAfterLookup_RefusesAndDoesNotOverwrite()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("rename-stale"));
        var stale = Slug(accountId);
        var committed = Target("won", accountId);
        var attempted = Target("old", accountId);
        var versionBefore = await VersionAsync(accountId);
        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var rename = Task.Run(() => RenameAsync(stale, attempted));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(rename, pid),
            "rename must reach and block on the source-row FOR UPDATE before the competing commit");
        Assert.False(rename.IsCompleted, "the source-row lock must actually hold the rename");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Accounts" SET "Slug" = {committed}, "Version" = "Version" + 1 WHERE "Id" = {accountId}""");
        await tx.CommitAsync();
        var outcome = await rename;

        Assert.False(outcome.Success);
        Assert.Equal("Account.SlugStale", outcome.ErrorCode);
        Assert.Equal(committed, await SlugAsync(accountId));
        Assert.Equal(versionBefore + 1, await VersionAsync(accountId));
        Assert.Equal(0, await factory.WithTenantScopeAsync(accountId, context => context.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
    }

    [Fact]
    public async Task Rename_ToTheSameCode_ChangesNothing()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("rename-noop"));
        var slug = Slug(accountId);
        var versionBefore = await VersionAsync(accountId);

        var outcome = await RenameAsync(slug, slug);

        Assert.True(outcome.Success);
        Assert.False(outcome.Changed);
        Assert.Equal(versionBefore, await VersionAsync(accountId));
        Assert.Equal(0, await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
    }

    [Fact]
    public async Task Rename_WithMissingSource_ReturnsNotFound()
    {
        var outcome = await RenameAsync("missing-" + Guid.NewGuid().ToString("N")[..11], "unused-target");
        Assert.False(outcome.Success);
        Assert.Equal("Accounts.NotFound", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rename_ToATakenCode_ReturnsSlugTaken_AndLeavesBothFarms()
    {
        var first = await factory.SeedAccountWithUserAsync(Unique("rename-taken-a"));
        var second = await factory.SeedAccountWithUserAsync(Unique("rename-taken-b"));

        var outcome = await RenameAsync(Slug(second), Slug(first));

        Assert.False(outcome.Success);
        Assert.Equal("Account.SlugTaken", outcome.ErrorCode);
        Assert.Equal(Slug(first), await SlugAsync(first));
        Assert.Equal(Slug(second), await SlugAsync(second));
    }

    [Fact]
    public async Task Rename_TwoFarmsRaceForOneCode_ExactlyOneWinsThroughTheIndexCatch()
    {
        var first = await factory.SeedAccountWithUserAsync(Unique("rename-race-a"));
        var second = await factory.SeedAccountWithUserAsync(Unique("rename-race-b"));
        var target = Target("race", first);
        var firstSlug = Slug(first);
        var secondSlug = Slug(second);
        var (firstDb, firstTx, firstPid) = await FenceAccountAsync(first);
        var (secondDb, secondTx, secondPid) = await FenceAccountAsync(second);
        await using var _1 = firstDb;
        await using var _2 = firstTx;
        await using var _3 = secondDb;
        await using var _4 = secondTx;

        var firstRename = Task.Run(() => RenameAsync(firstSlug, target));
        var secondRename = Task.Run(() => RenameAsync(secondSlug, target));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(firstRename, firstPid));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(secondRename, secondPid));
        Assert.False(firstRename.IsCompleted);
        Assert.False(secondRename.IsCompleted);
        // Each source row has one contender and each service's only blocking statement is
        // its source FOR UPDATE. These holder-specific waits prove both destination
        // pre-reads completed while target was still unclaimed.

        await firstTx.CommitAsync();
        await secondTx.CommitAsync();
        var outcomes = await Task.WhenAll(firstRename, secondRename);

        Assert.Single(outcomes, outcome => outcome.Success);
        Assert.Single(outcomes, outcome => outcome.ErrorCode == "Account.SlugTaken");
        Assert.Equal(1, await factory.WithTenantScopeAsync(first, db => db.Accounts
            .IgnoreQueryFilters().CountAsync(a => a.Slug == target)));
        Assert.Equal(1, await factory.WithTenantScopeAsync(first, db => db.AuditEvents
            .IgnoreQueryFilters().CountAsync(a =>
                (a.AccountId == first || a.AccountId == second) && a.Action == "Account.Rename")));
        var loser = outcomes[0].Success ? second : first;
        Assert.Equal(loser == first ? firstSlug : secondSlug, await SlugAsync(loser));
    }
}
