using System.Globalization;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;
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

    // #1228: models often lower-case a name they copy from a person's message.
    [Fact]
    public async Task Name_MatchesIgnoringCase()
    {
        var farm = await factory.SeedAccountWithUserAsync($"c-{Guid.NewGuid():N}@test.local");
        var name = $"Barn A {Guid.NewGuid():N}";
        var id = await SeedFlockAsync(farm, name);

        var found = Assert.IsType<FlockNameResolution.Found>(await ResolveAsync(farm, name.ToLowerInvariant()));
        Assert.Equal(new FlockReference(id, name, FlockStatus.Active), found.Flock);
    }

    [Fact]
    public async Task NamesDifferingOnlyInCase_AreAmbiguous()
    {
        var farm = await factory.SeedAccountWithUserAsync($"c-{Guid.NewGuid():N}@test.local");
        var suffix = Guid.NewGuid().ToString("N");
        var upper = await SeedFlockAsync(farm, $"Barn A {suffix}");
        var lower = await SeedFlockAsync(farm, $"barn a {suffix}");

        var ambiguous = Assert.IsType<FlockNameResolution.Ambiguous>(await ResolveAsync(farm, $"BARN A {suffix}"));
        Assert.Equal(new[] { upper, lower }.Order(), ambiguous.Candidates.Select(c => c.Id).Order());
    }

    // #1237 review R1: the database folds both sides. Lower-casing the input in .NET
    // under the invariant culture keeps 'İ' while PostgreSQL folds the stored 'İ' to
    // 'i', so the exact name stopped resolving.
    [Fact]
    public async Task UnicodeName_TypedExactly_ResolvesUnderAnInvariantCultureCaller()
    {
        var farm = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var name = $"İpek {Guid.NewGuid():N}";
        var id = await SeedFlockAsync(farm, name);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var found = Assert.IsType<FlockNameResolution.Found>(await ResolveAsync(farm, name));
            Assert.Equal(id, found.Flock.Id);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // The name is data, never a LIKE pattern: '_' and '%' match only themselves.
    [Fact]
    public async Task WildcardCharacters_MatchOnlyThemselves()
    {
        var farm = await factory.SeedAccountWithUserAsync($"w-{Guid.NewGuid():N}@test.local");
        var suffix = Guid.NewGuid().ToString("N");
        var underscore = await SeedFlockAsync(farm, $"Barn_1 {suffix}");
        await SeedFlockAsync(farm, $"BarnX1 {suffix}");
        var percent = await SeedFlockAsync(farm, $"Pen% {suffix}");
        await SeedFlockAsync(farm, $"Pen 12 {suffix}");

        Assert.Equal(underscore, Assert.IsType<FlockNameResolution.Found>(await ResolveAsync(farm, $"barn_1 {suffix}")).Flock.Id);
        Assert.Equal(percent, Assert.IsType<FlockNameResolution.Found>(await ResolveAsync(farm, $"pen% {suffix}")).Flock.Id);
        Assert.IsType<FlockNameResolution.NotFound>(await ResolveAsync(farm, $"pen% 12 {suffix}"));
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
