using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;
using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #852: IMortalityLedger only adds the movement to the caller's unit of work.
// The audit write comes after the append in Submit and Void, so faulting it
// proves the movement commits with the entry or not at all. The handlers run
// outside HTTP, where IdempotencyMiddleware's request transaction does not
// wrap Submit's single SaveChanges.
[Collection(IntegrationCollection.Name)]
public sealed class MortalityLedgerAtomicityTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record FlockDto(Guid Id, Guid FarmId, Guid HouseId);

    private async Task<(HttpClient Client, Guid AccountId, Guid FlockId, Guid EntryId)> DraftWithMortalityAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var create = await client.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = "Atomic flock", breed = "ISA", placementDate = "2026-01-01", initialCount = 100 });
        var flockId = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;
        var flock = (await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}"))!;

        // A zero-sellable day: no grade lines and no lots, so the movement is the
        // only row the submit adds besides the entry's own state change.
        var entry = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId = flock.FarmId,
                houseId = flock.HouseId,
                flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 50,
                crackedEggs = 0,
                dirtyEggs = 0,
                discardedEggs = 50,
                mortalityCount = 4,
            });
        return (client, accountId, flockId, (await entry.Content.ReadFromJsonAsync<IdDto>())!.Id);
    }

    private Task<(int Movements, DailyEntryStatus Status, int Version)> ReadAsync(
        Guid accountId, Guid flockId, Guid entryId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var entry = await db.DailyEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
            var movements = await db.BirdMovements.CountAsync(m => m.FlockId == flockId);
            return (movements, entry.Status, entry.Version);
        });

    [Fact]
    public async Task Submit_FailureAfterMortalityAppend_PersistsNothing()
    {
        var (_, accountId, flockId, entryId) = await DraftWithMortalityAsync();
        var before = await ReadAsync(accountId, flockId, entryId);

        using (var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId))
        {
            var handler = ActivatorUtilities.CreateInstance<SubmitDailyEntryHandler>(
                scope.ServiceProvider, new FaultingAuditWriter());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.HandleAsync(entryId, accountId, CancellationToken.None));
        }

        Assert.Equal((0, DailyEntryStatus.Draft), (before.Movements, before.Status));
        Assert.Equal(before, await ReadAsync(accountId, flockId, entryId));
    }

    [Fact]
    public async Task Void_FailureAfterMortalityReversal_PersistsNothing()
    {
        var (client, accountId, flockId, entryId) = await DraftWithMortalityAsync();
        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        submit.EnsureSuccessStatusCode();
        var before = await ReadAsync(accountId, flockId, entryId);

        using (var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId))
        {
            var handler = ActivatorUtilities.CreateInstance<VoidDailyEntryHandler>(
                scope.ServiceProvider, new FaultingAuditWriter());
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
                new VoidDailyEntryCommand(entryId, before.Version, "atomicity guard"), accountId, CancellationToken.None));
        }

        Assert.Equal((1, DailyEntryStatus.Submitted), (before.Movements, before.Status));
        Assert.Equal(before, await ReadAsync(accountId, flockId, entryId));
    }

    private sealed class FaultingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(string action, string entityType, Guid entityId, string? reason = null,
            object? details = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("#852 atomicity guard: faulting on the audit write.");
    }
}
