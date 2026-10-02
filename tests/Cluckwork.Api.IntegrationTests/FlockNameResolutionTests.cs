using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #852: name resolution reads through the filtered Flocks set, so the tenant
// and flock-scope filters decide the candidates (#613), and duplicate names
// come back as Ambiguous rather than a first match.
[Collection(IntegrationCollection.Name)]
public sealed class FlockNameResolutionTests(CluckworkWebApplicationFactory factory)
{
    private async Task<Guid> SeedFlockAsync(Guid accountId, string name)
    {
        var id = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Flocks.Add(Flock.Create(id, accountId, SeedDefaults.FarmId, SeedDefaults.HouseId,
                name, "ISA Brown", new DateOnly(2026, 1, 5), 100));
            await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<FlockNameResolution> ResolveAsync(Guid accountId, string name, IReadOnlyCollection<Guid>? assigned = null)
    {
        using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId);
        if (assigned is not null)
            scope.ServiceProvider.GetRequiredService<FlockScope>().Resolve(unrestricted: false, assigned);
        return await scope.ServiceProvider.GetRequiredService<IFlockLookup>().ResolveByNameAsync(name, CancellationToken.None);
    }

    [Fact]
    public async Task DuplicateName_IsAmbiguousWithThisFarmsFlocksInIdOrder()
    {
        var farmA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var farmB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");
        var name = $"Dup {Guid.NewGuid():N}";
        var first = await SeedFlockAsync(farmA, name);
        var second = await SeedFlockAsync(farmA, name);
        await SeedFlockAsync(farmB, name);

        var ambiguous = Assert.IsType<FlockNameResolution.Ambiguous>(await ResolveAsync(farmA, name));
        // Postgres orders uuid by its bytes, which is the order of the hex text.
        Assert.Equal(new[] { first, second }.OrderBy(id => id.ToString(), StringComparer.Ordinal),
            ambiguous.Candidates.Select(c => c.Id));
    }

    [Fact]
    public async Task DuplicateName_OutsideAWorkersScope_ResolvesToTheAssignedFlock()
    {
        var farm = await factory.SeedAccountWithUserAsync($"w-{Guid.NewGuid():N}@test.local");
        var name = $"Dup {Guid.NewGuid():N}";
        var assigned = await SeedFlockAsync(farm, name);
        await SeedFlockAsync(farm, name);

        var found = Assert.IsType<FlockNameResolution.Found>(await ResolveAsync(farm, name, [assigned]));
        Assert.Equal(new FlockReference(assigned, name, FlockStatus.Active), found.Flock);
    }

    [Fact]
    public async Task NameOnlyInAnotherFarm_IsNotFound()
    {
        var farmA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var farmB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");
        var name = $"Elsewhere {Guid.NewGuid():N}";
        await SeedFlockAsync(farmB, name);

        Assert.IsType<FlockNameResolution.NotFound>(await ResolveAsync(farmA, name));
    }
}
