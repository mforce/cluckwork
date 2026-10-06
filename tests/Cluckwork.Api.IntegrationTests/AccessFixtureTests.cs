using Cluckwork.Application.Modules.Access.Contracts;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #858 P5 — IAccessFixture writes a flock assignment without the interactive
// handler's step-up, so it is a security hold point. FixturePortRegistrationTests
// proves no Production host resolves it; these prove what it writes and what it
// refuses when called directly.
[Collection(IntegrationCollection.Name)]
public sealed class AccessFixtureTests(CluckworkWebApplicationFactory factory)
{
    private sealed record SeededFarm(Guid AccountId, Guid OwnerId, string OwnerEmail, Guid WorkerId, string WorkerEmail, Guid FlockId);

    private async Task<SeededFarm> SeedFarmAsync()
    {
        var ownerEmail = $"fixture-owner-{Guid.NewGuid():N}@test.local";
        var workerEmail = $"fixture-worker-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var flockId = await factory.SeedFlockAsync(accountId, SeedDefaults.FarmId);
        var (ownerId, workerId) = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.Users.Where(u => u.Email == ownerEmail).Select(u => u.Id).SingleAsync(),
             await db.Users.Where(u => u.Email == workerEmail).Select(u => u.Id).SingleAsync()));
        return new SeededFarm(accountId, ownerId, ownerEmail, workerId, workerEmail, flockId);
    }

    private async Task AssignAsync(SeededFarm actingFarm, Guid accountId, Guid userId, string email, Guid flockId)
    {
        using var scope = factory.Services.CreateScope()
            .ResolveTenantAndActor(actingFarm.AccountId, actingFarm.OwnerId, actingFarm.OwnerEmail);
        await scope.ServiceProvider.GetRequiredService<IAccessFixture>()
            .EnsureFlockAssignmentAsync(accountId, userId, email, flockId);
    }

    private async Task<(int Assignments, int AuditRows)> WrittenForAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.UserRoleAssignments.IgnoreQueryFilters().CountAsync(a => a.UserId == userId),
                await db.AuditEvents.IgnoreQueryFilters()
                    .CountAsync(e => e.EntityId == userId && e.Action == AuditActions.UserFlockAssign));
    }

    [Fact]
    public async Task AssignsOnce_AsTheActingOwner_NamingTheFlock()
    {
        var farm = await SeedFarmAsync();

        await AssignAsync(farm, farm.AccountId, farm.WorkerId, farm.WorkerEmail, farm.FlockId);
        await AssignAsync(farm, farm.AccountId, farm.WorkerId, farm.WorkerEmail, farm.FlockId);

        Assert.Equal((1, 1), await WrittenForAsync(farm.WorkerId));
        var (audit, flockName) = await factory.WithTenantScopeAsync(farm.AccountId, async db =>
            (await db.AuditEvents.SingleAsync(e => e.EntityId == farm.WorkerId),
             await db.Flocks.Where(f => f.Id == farm.FlockId).Select(f => f.Name).SingleAsync()));
        Assert.Equal(farm.OwnerEmail, audit.ActorEmail);
        Assert.Equal(farm.AccountId, audit.AccountId);
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal(farm.WorkerEmail, details.RootElement.GetProperty("email").GetString());
        Assert.Equal(flockName, details.RootElement.GetProperty("flock").GetString());
    }

    [Fact]
    public async Task RefusesAnotherFarmsUser()
    {
        var farm = await SeedFarmAsync();
        var other = await SeedFarmAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AssignAsync(farm, farm.AccountId, other.WorkerId, other.WorkerEmail, farm.FlockId));

        Assert.Equal((0, 0), await WrittenForAsync(other.WorkerId));
    }

    [Fact]
    public async Task RefusesAnotherFarmsFlock()
    {
        var farm = await SeedFarmAsync();
        var other = await SeedFarmAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AssignAsync(farm, farm.AccountId, farm.WorkerId, farm.WorkerEmail, other.FlockId));

        Assert.Equal((0, 0), await WrittenForAsync(farm.WorkerId));
    }

    // The farm named by accountId owns the user, but the resolved tenant is another
    // farm whose flock is visible: the tenant stamp refuses the row.
    [Fact]
    public async Task RefusesAFarmOtherThanTheResolvedTenant()
    {
        var farm = await SeedFarmAsync();
        var other = await SeedFarmAsync();

        await Assert.ThrowsAsync<TenantWriteMismatchException>(() =>
            AssignAsync(farm, other.AccountId, other.WorkerId, other.WorkerEmail, farm.FlockId));

        Assert.Equal((0, 0), await WrittenForAsync(other.WorkerId));
    }
}
