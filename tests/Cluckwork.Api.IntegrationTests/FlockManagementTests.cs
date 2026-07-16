namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #47 — flock management: update/deplete/archive over the API, archived
// exclusion from default lists, capture gating on flock status, and the
// Version-token race (AGENTS.md aggregate-mutation rule).
[Collection(IntegrationCollection.Name)]
public sealed class FlockManagementTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record FlockDto(
        Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed,
        DateOnly PlacementDate, int InitialCount, string Status);

    private async Task<(HttpClient Client, Guid AccountId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        return (factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email)), accountId);
    }

    private static Task<HttpResponseMessage> PutWithKeyAsync(
        HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<Guid> CreateFlockAsync(HttpClient client, string name = "Barn A")
    {
        var response = await client.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name, breed = "ISA Brown", placementDate = "2026-01-01", initialCount = 200 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    [Fact]
    public async Task Flock_UpdateDepleteArchive_FullLoop()
    {
        var (client, _) = await SetupAsync();
        var id = await CreateFlockAsync(client);

        var update = await PutWithKeyAsync(client, $"/api/v1/flocks/{id}",
            new { name = "  Barn A West ", breed = "Lohmann Brown", placementDate = "2026-02-01", initialCount = 180 });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var got = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{id}");
        Assert.Equal("Barn A West", got!.Name);      // trimmed
        Assert.Equal("Lohmann Brown", got.Breed);
        Assert.Equal(new DateOnly(2026, 2, 1), got.PlacementDate);
        Assert.Equal(180, got.InitialCount);

        var deplete = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{id}/deplete", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, deplete.StatusCode);

        // Double-deplete conflicts with current state.
        var again = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{id}/deplete", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Depleted flocks stay in the default list; archived leave it.
        var beforeArchive = await client.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks");
        Assert.Contains(beforeArchive!, f => f.Id == id && f.Status == "Depleted");

        var archive = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{id}/archive", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        var defaultList = await client.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks");
        Assert.DoesNotContain(defaultList!, f => f.Id == id);
        var managementList = await client.GetFromJsonAsync<List<FlockDto>>(
            "/api/v1/flocks?includeArchived=true");
        Assert.Contains(managementList!, f => f.Id == id && f.Status == "Archived");

        var rearchive = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{id}/archive", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, rearchive.StatusCode);
    }

    [Fact]
    public async Task Flock_ArchiveFromActive_Allowed()
    {
        // Mistake-created flocks shouldn't need a fake depletion first.
        var (client, _) = await SetupAsync();
        var id = await CreateFlockAsync(client, "Oops");
        var archive = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{id}/archive", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
    }

    [Fact]
    public async Task DailyEntry_OnDepletedFlock_Rejected()
    {
        // Depleted birds lay no eggs — capture must refuse (#47). The flock and
        // its farm come from the API, so this runs the same path the SPA does.
        var (client, _) = await SetupAsync();
        var id = await CreateFlockAsync(client);
        var flock = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{id}");

        object EntryBody(string date) => new
        {
            farmId = flock!.FarmId,
            houseId = flock.HouseId,
            flockId = id,
            date,
            totalEggs = 10,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = 0,
        };

        var ok = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody("2026-07-14"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        await client.PostWithKeyAsync($"/api/v1/flocks/{id}/deplete", Guid.NewGuid().ToString());

        var rejected = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody("2026-07-15"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        // Unknown flock -> 404, distinct from the status rejection.
        var unknown = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId = flock!.FarmId, houseId = flock.HouseId, flockId = Guid.NewGuid(),
                date = "2026-07-15", totalEggs = 10, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 0,
            });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Flock_ParallelUpdates_NoTornWrite_VersionAdvances()
    {
        var (client, accountId) = await SetupAsync();
        var id = await CreateFlockAsync(client, "Race");

        var a = PutWithKeyAsync(client, $"/api/v1/flocks/{id}",
            new { name = "Race A", breed = "Breed A", placementDate = "2026-01-01", initialCount = 100 });
        var b = PutWithKeyAsync(client, $"/api/v1/flocks/{id}",
            new { name = "Race B", breed = "Breed B", placementDate = "2026-02-02", initialCount = 200 });
        var responses = await Task.WhenAll(a, b);

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
            $"unexpected {(int)r.StatusCode}"));
        var successes = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.True(successes >= 1);

        var final = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{id}");
        var isA = final is { Name: "Race A", Breed: "Breed A", InitialCount: 100 };
        var isB = final is { Name: "Race B", Breed: "Breed B", InitialCount: 200 };
        Assert.True(isA || isB, $"torn write: {final!.Name}/{final.Breed}/{final.InitialCount}");

        // Full-payload writes mask a missing token (grade-review lesson) — pin
        // the Version itself: one bump per successful mutation.
        var version = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.Flocks.FirstAsync(f => f.Id == id)).Version);
        Assert.Equal(successes, version);
    }
}
