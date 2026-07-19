namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #93 — the audit trail is domain data: written in the same transaction as
// the change (a failed action leaves nothing), actor captured from the JWT,
// viewer admin-only.
[Collection(IntegrationCollection.Name)]
public sealed class AuditTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record EntryDto(Guid Id, int Version);
    private sealed record AuditRow(
        Guid Id, DateTimeOffset OccurredAtUtc, string ActorEmail,
        string Action, string EntityType, Guid EntityId,
        string? Reason, string? DetailsJson);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Client, string Email, Guid AccountId, Guid FarmId, Guid FlockId, Guid GradeId)>
        SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, email, accountId, farmId, flockId, grades["Large"]);
    }

    [Fact]
    public async Task Adjust_WritesEvent_WithActorAndReason_FailedAdjustWritesNothing()
    {
        var (client, email, _, farmId, flockId, gradeId) = await SetupAsync();

        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = Today,
            totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = gradeId, quantity = 90 } }
        });
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var version = (await client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{entryId}"))!.Version;

        // A FAILED adjust (stale version → 409) must leave no event.
        var failed = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(), new
            {
                version = version + 7, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 0, reason = "stale"
            });
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);

        var afterFailure = await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?entityId={entryId}");
        Assert.Empty(afterFailure!);

        // The successful adjust writes exactly one, with actor + reason.
        var ok = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(), new
            {
                version, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 0, reason = "recount at pickup"
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var rows = await client.GetFromJsonAsync<List<AuditRow>>($"/api/v1/audit?entityId={entryId}");
        var row = Assert.Single(rows!);
        Assert.Equal("DailyEntry.Adjust", row.Action);
        Assert.Equal("DailyEntry", row.EntityType);
        Assert.Equal(email, row.ActorEmail);
        Assert.Equal("recount at pickup", row.Reason);
    }

    [Fact]
    public async Task CriticalActions_LandInTheTrail_FilteredByAction()
    {
        var (client, _, _, farmId, flockId, _) = await SetupAsync();

        // Flock edit + cull + deplete → three distinct actions.
        var flockGet = await client.GetAsync($"/api/v1/flocks/{flockId}");
        Assert.Equal(HttpStatusCode.OK, flockGet.StatusCode);
        var putBody = new
        {
            name = "Audited flock", breed = "Test Breed",
            placementDate = Today.AddDays(-30), initialCount = 100
        };
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/flocks/{flockId}")
        { Content = JsonContent.Create(putBody) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { type = "Cull", quantity = 3, date = Today, note = "culled sick birds" });
        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/deplete", Guid.NewGuid().ToString());

        var updates = await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Flock.Update");
        Assert.Contains(updates!, r => r.EntityId == flockId);

        var movements = await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Flock.BirdMovement");
        var cull = Assert.Single(movements!, r => r.EntityId == flockId);
        Assert.Equal("culled sick birds", cull.Reason);
        Assert.Contains("Cull", cull.DetailsJson);

        var depletions = await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Flock.Deplete");
        Assert.Contains(depletions!, r => r.EntityId == flockId);
    }

    [Fact]
    public async Task Viewer_IsAdminOnly()
    {
        var (adminClient, _, accountId, _, _, _) = await SetupAsync();
        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/audit")).StatusCode);
    }
}
