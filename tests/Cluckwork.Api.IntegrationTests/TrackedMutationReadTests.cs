namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #561 review — the write guard's Modified/Deleted checks compare AccountId's
// ORIGINAL value against the resolved tenant, and that is only meaningful while
// the original value is the DATABASE's.
//
// It stops being the database's the moment an entity reaches SaveChanges
// detached: DbSet.Update and DbSet.Remove attach the instance and seed its
// original values from the caller's own current values, so a hand-built stub
// carrying another tenant's primary key and this tenant's AccountId would pass
// both halves of the check.
//
// Every repository mutation read is a TRACKED read behind the tenant query
// filter, which is what makes the snapshot trustworthy. That is a PRECONDITION
// of the guard, not an incidental detail, so it gets a test: flipping one of
// these reads to AsNoTracking is exactly the change that would void the theft
// protection, and it must fail here rather than pass quietly.
//
// Deliberately NOT a test that a detached write succeeds. That behaviour is a
// known gap tracked in #562; asserting it would turn "not yet fixed" into
// "specified".
[Collection(IntegrationCollection.Name)]
public sealed class TrackedMutationReadTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task FlockRepository_GetByIdAsync_ReturnsATrackedEntity()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"t-{Guid.NewGuid():N}@test.local");

        var flockId = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = Flock.Create(Guid.NewGuid(), accountId, Guid.NewGuid(), Guid.NewGuid(),
                "Tracked Read Flock", "Breed", DateOnly.FromDateTime(DateTime.UtcNow.Date), 10);
            db.Flocks.Add(flock);
            await db.SaveChangesAsync();
            return flock.Id;
        });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var repo = scope.ServiceProvider.GetRequiredService<IFlockRepository>();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var loaded = await repo.GetByIdAsync(flockId);

        Assert.NotNull(loaded);
        // Tracked, and Unchanged — i.e. EF holds a real database snapshot for
        // it, which is what the guard's OriginalValue comparison relies on.
        Assert.Equal(EntityState.Unchanged, db2.Entry(loaded!).State);
        Assert.Equal(accountId, db2.Entry(loaded!).Property(nameof(Flock.AccountId)).OriginalValue);
    }
}
