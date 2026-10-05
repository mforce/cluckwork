using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #852: the seeder finds its flocks by name, and duplicate names are legal. A
// second flock sharing an operational flock's name must stop the re-run at the
// lookup, naming both flocks, instead of seeding against whichever row the
// database returned first.
public sealed class SimulationDuplicateFlockNameTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task SimulationSeed_WhenAnOperationalFlockNameIsDuplicated_RefusesTheRerunNamingBoth()
    {
        var first = await factory.SeedOnceAsync();
        Assert.True(first.IsSuccess, $"first seed failed: {first.Message}");

        var duplicateId = Guid.NewGuid();
        var originalId = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
        {
            var original = await db.Flocks.SingleAsync(f => f.Name == "Sim House B");
            db.Flocks.Add(Flock.Create(duplicateId, SeedDefaults.AccountId, SeedDefaults.FarmId, SeedDefaults.HouseId,
                "Sim House B", "Lohmann Brown", original.PlacementDate, 100));
            await db.SaveChangesAsync();
            return original.Id;
        });

        var rerun = await factory.SeedOnceAsync();

        Assert.Equal(SeedStatus.Failed, rerun.Status);
        Assert.Contains("2 flocks are named Sim House B", rerun.Message);
        Assert.Contains(originalId.ToString(), rerun.Message);
        Assert.Contains(duplicateId.ToString(), rerun.Message);
        // The lookup refused, not the manifest's count check after every other phase ran.
        Assert.DoesNotContain("expected", rerun.Message);
    }
}
