using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #797 — the OAuth half of the housekeeping sweep: dead tokens and authorizations go
// after the retention, unapproved registrations after their window, live connections
// never. Rows are aged by rewriting their creation time, so nothing waits on a clock.
// The host's own worker is removed (NoHostWorkerFactory): it would sweep these rows on
// its own poll and race the leader-gate test below.
public sealed class OAuthPurgeTests(NoHostWorkerFactory factory) : IClassFixture<NoHostWorkerFactory>
{
    private readonly OAuthServerTests flows = new(factory);

    [Fact]
    public async Task DeadRowsPastRetention_ArePruned_AndALiveConnectionSurvives()
    {
        var live = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var revoked = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await flows.ConnectAsync(factory, live);
        await flows.ConnectAsync(factory, revoked);
        await RevokeAsync(revoked);
        var aged = OAuthPurgeSweep.PruneRetention + TimeSpan.FromDays(1);
        await AgeAsync(live, aged);
        await AgeAsync(revoked, aged);

        var failure = await Record.ExceptionAsync(PurgeNowAsync);

        Assert.True(failure is null, $"the purge threw: {failure}");
        var liveRows = await RowsAsync(live);
        Assert.True(liveRows.Application, "the live connection's application was deleted");
        Assert.Equal([Statuses.Valid], liveRows.Authorizations);
        // Only the redeemed code goes; the access token it was exchanged for stays.
        Assert.Equal([(TokenTypeIdentifiers.AccessToken, Statuses.Valid)], liveRows.Tokens);
        var revokedRows = await RowsAsync(revoked);
        Assert.Empty(revokedRows.Tokens);
        Assert.Empty(revokedRows.Authorizations);
        Assert.False(revokedRows.Application, "a revoked connection's application outlived its approval");
    }

    [Fact]
    public async Task DeadRowsInsideRetention_AreKept()
    {
        var revoked = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await flows.ConnectAsync(factory, revoked);
        await RevokeAsync(revoked);
        await AgeAsync(revoked, OAuthPurgeSweep.PruneRetention - TimeSpan.FromDays(1));

        await PurgeNowAsync();

        var rows = await RowsAsync(revoked);
        Assert.True(rows.Tokens.Count == 2, "a revoked token was pruned inside the retention");
        Assert.Equal([Statuses.Revoked], rows.Authorizations);
    }

    [Fact]
    public async Task UnapprovedApplication_IsDeletedOnlyAfterTheWindow()
    {
        var expired = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var fresh = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await AgeApplicationAsync(expired, OAuthPurgeSweep.UnapprovedWindow + TimeSpan.FromHours(1));
        await AgeApplicationAsync(fresh, OAuthPurgeSweep.UnapprovedWindow - TimeSpan.FromHours(1));

        await PurgeNowAsync();

        Assert.False((await RowsAsync(expired)).Application, "an unapproved application outlived its window");
        Assert.True((await RowsAsync(fresh)).Application, "an unapproved application was deleted inside its window");
    }

    // #271: a follower runs nothing, so only the leader deletes.
    [Fact]
    public async Task Sweep_RunsOnlyOnTheLeader()
    {
        var expired = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var fresh = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await AgeApplicationAsync(expired, OAuthPurgeSweep.UnapprovedWindow + TimeSpan.FromHours(1));

        await RunWorkerOnceAsync(LeaseStatus.Follower);
        Assert.True((await RowsAsync(expired)).Application, "a follower ran the OAuth sweep");

        await RunWorkerOnceAsync(LeaseStatus.Leader);
        Assert.False((await RowsAsync(expired)).Application, "the leader did not run the OAuth sweep");
        Assert.True((await RowsAsync(fresh)).Application, "the sweep deleted an application inside its window");
    }

    private async Task PurgeNowAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOAuthPurge>().PurgeAsync(
            DateTimeOffset.UtcNow - OAuthPurgeSweep.PruneRetention, OAuthPurgeSweep.UnapprovedWindow, CancellationToken.None);
    }

    private Task RunWorkerOnceAsync(LeaseStatus lease)
    {
        var signallingLease = new SignallingLease(lease);
        var worker = new DurableJobWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DurableJobWorker>.Instance,
            signallingLease,
            oauthPurgeSweep: factory.Services.GetRequiredService<OAuthPurgeSweep>(),
            pollInterval: TimeSpan.FromMilliseconds(20));
        return signallingLease.RunWorkerThroughOneIterationAsync(worker);
    }

    // What disconnecting an app will do (#799): revoke the approval and its tokens.
    private async Task RevokeAsync(string clientId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var application = await applications.FindByClientIdAsync(clientId)
            ?? throw new InvalidOperationException($"no application {clientId}");
        await foreach (var authorization in authorizations.FindByApplicationIdAsync(
                           (await applications.GetIdAsync(application))!))
        {
            Assert.True(await authorizations.TryRevokeAsync(authorization));
            await tokens.RevokeByAuthorizationIdAsync((await authorizations.GetIdAsync(authorization))!);
        }
    }

    // OpenIddict stamps tokens and authorizations from the API's clock.
    private async Task AgeAsync(string clientId, TimeSpan age)
    {
        await AgeApplicationAsync(clientId, age);
        var createdAt = DateTimeOffset.UtcNow - age;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OpenIddictTokens" SET "CreationDate" = {createdAt}
            WHERE "ApplicationId" = (SELECT "Id" FROM "OpenIddictApplications" WHERE "ClientId" = {clientId})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OpenIddictAuthorizations" SET "CreationDate" = {createdAt}
            WHERE "ApplicationId" = (SELECT "Id" FROM "OpenIddictApplications" WHERE "ClientId" = {clientId})
            """);
    }

    // The #819 trigger keeps CreatedAtUtc on update, so aging an application switches it
    // off inside one transaction, as the #819 tests do. The age is measured on the
    // database's clock, the one that stamped the row.
    private async Task AgeApplicationAsync(string clientId, TimeSpan age)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "OpenIddictApplications" DISABLE TRIGGER "TR_OpenIddictApplications_BusinessRecordTimestamps";""");
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "OpenIddictApplications" SET "CreatedAtUtc" = now() - {age} WHERE "ClientId" = {clientId}""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "OpenIddictApplications" ENABLE TRIGGER "TR_OpenIddictApplications_BusinessRecordTimestamps";""");
        await transaction.CommitAsync();
        Assert.Equal(1, updated);
    }

    private async Task<(bool Application, List<string?> Authorizations, List<(string?, string?)> Tokens)> RowsAsync(
        string clientId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var application = await db.OAuthApplications.AnyAsync(app => app.ClientId == clientId);
        var authorizations = await db.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>()
            .Where(authorization => authorization.Application!.ClientId == clientId)
            .Select(authorization => authorization.Status)
            .ToListAsync();
        var tokens = await db.Set<OpenIddictEntityFrameworkCoreToken<Guid>>()
            .Where(token => token.Application!.ClientId == clientId)
            .Select(token => new { token.Type, token.Status })
            .ToListAsync();
        return (application, authorizations, [.. tokens.Select(token => (token.Type, token.Status))]);
    }
}
