using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #857 — IAccessLookup serves the flock-scope middleware, FlockScopeGuard and
// ConfirmSaleHandler. The existing suites never put two workers with different
// assignments in one farm, nor give one worker both a flock row and a farm-wide
// row, so a lookup that returned the whole farm's rows, or dropped farm-wide
// rows, passed them. These tests seed exactly those shapes.
[Collection(IntegrationCollection.Name)]
public sealed class AccessLookupTests(CluckworkWebApplicationFactory factory)
{
    private sealed record FlockRow(Guid Id);

    private sealed record SeededFarm(Guid AccountId, Guid FarmId, Guid FlockA, Guid FlockB);

    private async Task<SeededFarm> SeedFarmAsync()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"o-{Guid.NewGuid():N}@test.local");
        var farmId = Guid.NewGuid();
        return new SeededFarm(accountId, farmId,
            await factory.SeedFlockAsync(accountId, farmId),
            await factory.SeedFlockAsync(accountId, farmId));
    }

    // A null flock seeds a farm-wide row: FlockId null, FarmId set.
    private async Task<(Guid Id, string Email)> SeedWorkerAsync(SeededFarm farm, params Guid?[] flocks)
    {
        var accountId = farm.AccountId;
        var email = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, (string?)null);
        var id = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            foreach (var flockId in flocks)
                db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                    Guid.NewGuid(), accountId, userId, flockId is null ? farm.FarmId : null, houseId: null, flockId));
            await db.SaveChangesAsync();
            return userId;
        });
        return (id, email);
    }

    private async Task<HashSet<Guid>> VisibleFlocksAsync(string email)
    {
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var response = await client.GetAsync("/api/v1/flocks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<FlockRow>>())!.Select(f => f.Id).ToHashSet();
    }

    [Fact]
    public async Task GetAssignedFlocks_ReturnsNullForMixedFarmWideRowsAndOnlyThatUsersAssignedSet()
    {
        var farm = await SeedFarmAsync();
        var (x, _) = await SeedWorkerAsync(farm, farm.FlockA, null);
        var (y, _) = await SeedWorkerAsync(farm, farm.FlockA, farm.FlockB);

        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(farm.AccountId);
        var lookup = scope.ServiceProvider.GetRequiredService<IAccessLookup>();

        var forX = await lookup.GetAssignedFlocksAsync(x);
        var forY = await lookup.GetAssignedFlocksAsync(y);

        Assert.Null(forX);
        Assert.Equal(new HashSet<Guid> { farm.FlockA, farm.FlockB }, forY);
        Assert.Null(await lookup.GetAssignedFlocksAsync(Guid.NewGuid()));
    }

    // One email holds a different role in each of two farms. The role read is
    // scoped by the account it is asked about, never by the user id alone.
    [Fact]
    public async Task GetEffectiveRole_AnswersOnlyForTheAccountItIsAskedAbout()
    {
        var email = $"both-{Guid.NewGuid():N}@test.local";
        var farmA = await SeedFarmAsync();
        var farmB = await SeedFarmAsync();
        await factory.SeedUserAsync(farmA.AccountId, email, Roles.Manager);
        await factory.SeedUserAsync(farmB.AccountId, email, Roles.Owner);
        var inA = await factory.WithTenantScopeAsync(farmA.AccountId, db =>
            db.Users.Where(u => u.Email == email && u.AccountId == farmA.AccountId).Select(u => u.Id).SingleAsync());
        var inB = await factory.WithTenantScopeAsync(farmB.AccountId, db =>
            db.Users.Where(u => u.Email == email && u.AccountId == farmB.AccountId).Select(u => u.Id).SingleAsync());

        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(farmB.AccountId);
        var lookup = scope.ServiceProvider.GetRequiredService<IAccessLookup>();

        Assert.Equal(EffectiveAccountRole.Owner, await lookup.GetEffectiveRoleAsync(farmB.AccountId, inB));
        Assert.Null(await lookup.GetEffectiveRoleAsync(farmB.AccountId, inA));
        Assert.Equal(EffectiveAccountRole.Manager, await lookup.GetEffectiveRoleAsync(farmA.AccountId, inA));
        Assert.Null(await lookup.GetEffectiveRoleAsync(farmA.AccountId, inB));
    }

    [Fact]
    public async Task GetAssignedFlocks_RefusesAnUnresolvedTenant()
    {
        var farm = await SeedFarmAsync();
        var (worker, _) = await SeedWorkerAsync(farm, farm.FlockA);

        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IAccessLookup>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => lookup.GetAssignedFlocksAsync(worker));
    }

    [Fact]
    public async Task GetAssignedFlocks_UsesTheScopedContextsTenantAndForwardsCancellation()
    {
        var farmA = await SeedFarmAsync();
        var farmB = await SeedFarmAsync();
        var (workerA, _) = await SeedWorkerAsync(farmA, farmA.FlockA);
        var (workerB, _) = await SeedWorkerAsync(farmB, farmB.FlockB);
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IAccessLookup>();
        scope.ResolveTenantAndActor(farmA.AccountId);

        Assert.Equal(new HashSet<Guid> { farmA.FlockA }, await lookup.GetAssignedFlocksAsync(workerA));
        Assert.Null(await lookup.GetAssignedFlocksAsync(workerB));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lookup.GetAssignedFlocksAsync(workerA, cancellation.Token));
    }

    [Fact]
    public async Task ARestrictedWorker_DoesNotInheritAnotherWorkersFarmWideRow()
    {
        var farm = await SeedFarmAsync();
        var (_, restricted) = await SeedWorkerAsync(farm, farm.FlockA);
        await SeedWorkerAsync(farm, (Guid?)null);

        Assert.Equal(new HashSet<Guid> { farm.FlockA }, await VisibleFlocksAsync(restricted));
    }

    [Fact]
    public async Task AWorkerWithAFlockRowAndAFarmWideRow_IsUnrestricted()
    {
        var farm = await SeedFarmAsync();
        var (_, worker) = await SeedWorkerAsync(farm, farm.FlockA, null);

        Assert.Equal(new HashSet<Guid> { farm.FlockA, farm.FlockB }, await VisibleFlocksAsync(worker));
    }
}
