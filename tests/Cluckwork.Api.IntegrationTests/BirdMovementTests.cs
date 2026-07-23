namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #54 — bird movement ledger: mortality generated at submit, manual
// culls/adjustments, current-count math, and lifecycle gating.
[Collection(IntegrationCollection.Name)]
public sealed class BirdMovementTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record FlockDto(
        Guid Id, Guid FarmId, Guid HouseId, string Name,
        int InitialCount, long CurrentBirds, string Status);
    private sealed record MovementDto(
        Guid Id, Guid FlockId, DateOnly Date, string Type, int Quantity, string? Note);

    private async Task<(HttpClient Client, Guid AccountId, Guid FlockId, FlockDto Flock)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var create = await client.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = "Ledger flock", breed = "ISA", placementDate = "2026-01-01", initialCount = 100 });
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;
        var flock = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{id}");
        return (client, accountId, id, flock!);
    }

    private static object EntryBody(FlockDto f, DateOnly date, int mortality) => new
    {
        farmId = f.FarmId,
        houseId = f.HouseId,
        flockId = f.Id,
        date,
        totalEggs = 50,
        crackedEggs = 0,
        dirtyEggs = 0,
        discardedEggs = 0,
        mortalityCount = mortality,
    };

    [Fact]
    public async Task Submit_GeneratesMortalityMovement_CountDrops()
    {
        var (client, accountId, flockId, flock) = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Day with mortality: submit -> movement + count drop.
        var e1 = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody(flock, today, 5));
        var id1 = (await e1.Content.ReadFromJsonAsync<IdDto>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{id1}/submit", Guid.NewGuid().ToString());

        // Day without mortality: submit -> nothing added.
        var e2 = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody(flock, today.AddDays(-1), 0));
        var id2 = (await e2.Content.ReadFromJsonAsync<IdDto>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{id2}/submit", Guid.NewGuid().ToString());

        var after = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}");
        Assert.Equal(95, after!.CurrentBirds);

        var movements = await client.GetFromJsonAsync<List<MovementDto>>(
            $"/api/v1/flocks/{flockId}/movements");
        var row = Assert.Single(movements!);
        Assert.Equal("Mortality", row.Type);
        Assert.Equal(5, row.Quantity);
        Assert.Equal(today, row.Date);

        // Generated rows carry the originating entry id (reconciliation hook).
        var backRef = await factory.WithTenantScopeAsync(accountId, db =>
            db.BirdMovements.SingleAsync(m => m.FlockId == flockId));
        Assert.Equal(id1, backRef.DailyEntryId);

        // Draft mortality is NOT a movement yet: record without submitting.
        await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody(flock, today.AddDays(-2), 7));
        var still = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}");
        Assert.Equal(95, still!.CurrentBirds);
    }

    [Fact]
    public async Task ManualCullAndAdjustment_CountMath()
    {
        var (client, _, flockId, _) = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var cull = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today, type = "Cull", quantity = 10, note = "sold spent hens" });
        Assert.Equal(HttpStatusCode.Created, cull.StatusCode);

        // Negative adjustment adds birds back (miscount correction).
        var adjust = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today, type = "Adjustment", quantity = -3, note = "recount" });
        Assert.Equal(HttpStatusCode.Created, adjust.StatusCode);

        var flock = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}");
        Assert.Equal(93, flock!.CurrentBirds);   // 100 - 10 + 3

        // List view carries the same number via the grouped query.
        var list = await client.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks");
        Assert.Equal(93, list!.Single(f => f.Id == flockId).CurrentBirds);
    }

    [Fact]
    public async Task Movement_Validation_And_LifecycleGating()
    {
        var (client, _, flockId, _) = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Manual Mortality would double-count with submitted entries -> 400.
        var mortality = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today, type = "Mortality", quantity = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, mortality.StatusCode);

        // Zero quantity / negative cull -> 400.
        var zero = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today, type = "Cull", quantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        var negativeCull = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today, type = "Cull", quantity = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, negativeCull.StatusCode);

        // Unknown flock -> 404 (write and read).
        var unknown = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{Guid.NewGuid()}/movements", Guid.NewGuid().ToString(),
            new { date = today, type = "Cull", quantity = 1 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var unknownList = await client.GetAsync($"/api/v1/flocks/{Guid.NewGuid()}/movements");
        Assert.Equal(HttpStatusCode.NotFound, unknownList.StatusCode);

        // Archived flock accepts nothing; depleted accepts backfill only.
        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/deplete", Guid.NewGuid().ToString());
        var backfill = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today.AddDays(-1), type = "Cull", quantity = 1 });
        Assert.Equal(HttpStatusCode.Created, backfill.StatusCode);
        // Tomorrow is refused up front (400) rather than by the lifecycle rule:
        // #35 replaced the validator's +1-day UTC slack with the farm-local
        // boundary, so a future date never reaches the depleted-flock check.
        var late = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today.AddDays(1), type = "Cull", quantity = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);

        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/archive", Guid.NewGuid().ToString());
        var archived = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { date = today.AddDays(-1), type = "Cull", quantity = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, archived.StatusCode);
    }

    [Fact]
    public async Task Submit_OfArchivedFlockDraft_Rejected()
    {
        // Recording gates on lifecycle; submit must too, or an archived flock's
        // leftover draft could still mint lots + a mortality row.
        var (client, _, flockId, flock) = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var create = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody(flock, today, 3));
        var entryId = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/archive", Guid.NewGuid().ToString());

        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, submit.StatusCode);

        // Depleted + backfill date: submit still allowed (late final-day entry).
        var (client2, _, flockId2, flock2) = await SetupAsync();
        var create2 = await client2.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(flock2, today.AddDays(-1), 2));
        var entryId2 = (await create2.Content.ReadFromJsonAsync<IdDto>())!.Id;
        await client2.PostWithKeyAsync($"/api/v1/flocks/{flockId2}/deplete", Guid.NewGuid().ToString());
        var submit2 = await client2.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId2}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit2.StatusCode);
    }

    [Fact]
    public async Task ParallelSubmits_ExactlyOneMortalityMovement()
    {
        // The submit race (entry Version token) must also protect the generated
        // movement: the losing submit's movement rolls back with its lots.
        var (client, accountId, flockId, flock) = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var create = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody(flock, today, 4));
        var entryId = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var a = client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var b = client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        await Task.WhenAll(a, b);

        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.BirdMovements.CountAsync(m => m.FlockId == flockId));
        Assert.Equal(1, count);

        var f = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}");
        Assert.Equal(96, f!.CurrentBirds);
    }
}
