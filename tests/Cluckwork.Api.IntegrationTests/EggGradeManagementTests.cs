namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #42 — egg grade management: CRUD over the API, case-insensitive name
// uniqueness, deactivation semantics, and the Version-token race required by
// the aggregate-mutation rule (AGENTS.md).
[Collection(IntegrationCollection.Name)]
public sealed class EggGradeManagementTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record GradeDto(
        Guid Id, Guid FarmId, string Name, string GradeType, int SortOrder, bool IsSaleable, bool Active);

    // API-created grades land in the seeded single-MVP farm, same as flocks.
    private async Task<(HttpClient Client, Guid AccountId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        return (factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email)), accountId);
    }

    private async Task<HttpClient> SetupClientAsync() => (await SetupAsync()).Client;

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

    [Fact]
    public async Task Grade_CreateUpdateDeactivateActivate_FullLoop()
    {
        var client = await SetupClientAsync();

        var create = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "  Pullet Eggs ", gradeType = "custom", sortOrder = 42, isSaleable = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var listed = await client.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades");
        var grade = Assert.Single(listed!, g => g.Id == id);
        Assert.Equal("Pullet Eggs", grade.Name);          // trimmed
        Assert.Equal("Custom", grade.GradeType);          // parsed case-insensitively
        Assert.Equal(42, grade.SortOrder);
        Assert.True(grade.Active);

        var update = await PutWithKeyAsync(client, $"/api/v1/egg-grades/{id}",
            new { name = "Pullet", sortOrder = 7, isSaleable = false });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var deactivate = await client.PostWithKeyAsync(
            $"/api/v1/egg-grades/{id}/deactivate", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        // Default list hides it; the management view still shows it, inactive.
        var activeOnly = await client.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades");
        Assert.DoesNotContain(activeOnly!, g => g.Id == id);
        var all = await client.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades?includeInactive=true");
        var inactive = Assert.Single(all!, g => g.Id == id);
        Assert.False(inactive.Active);
        Assert.Equal("Pullet", inactive.Name);
        Assert.Equal(7, inactive.SortOrder);
        Assert.False(inactive.IsSaleable);

        // Deactivating twice conflicts with current state.
        var again = await client.PostWithKeyAsync(
            $"/api/v1/egg-grades/{id}/deactivate", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var activate = await client.PostWithKeyAsync(
            $"/api/v1/egg-grades/{id}/activate", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        var reactivated = await client.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades");
        Assert.Contains(reactivated!, g => g.Id == id);
    }

    [Fact]
    public async Task Grade_DuplicateName_CaseInsensitive_Conflicts()
    {
        var client = await SetupClientAsync();

        var first = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Washed", gradeType = "Quality", sortOrder = 1, isSaleable = true });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same name, different casing + padding — rejected.
        var dup = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "  wAsHeD ", gradeType = "Quality", sortOrder = 2, isSaleable = true });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // Renaming another grade onto it — rejected too.
        var other = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Unwashed", gradeType = "Quality", sortOrder = 3, isSaleable = true });
        var otherId = (await other.Content.ReadFromJsonAsync<IdDto>())!.Id;
        var rename = await PutWithKeyAsync(client, $"/api/v1/egg-grades/{otherId}",
            new { name = "WASHED", sortOrder = 3, isSaleable = true });
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);

        // Renaming a grade to its own name (case change only) is fine —
        // the duplicate check excludes the grade being renamed.
        var selfRename = await PutWithKeyAsync(client, $"/api/v1/egg-grades/{otherId}",
            new { name = "UNWASHED", sortOrder = 3, isSaleable = true });
        Assert.Equal(HttpStatusCode.NoContent, selfRename.StatusCode);
    }

    [Fact]
    public async Task Grade_Deactivated_RejectedByDailyEntryCapture()
    {
        var client = await SetupClientAsync();

        // Flock + grade both land in the seeded MVP farm, so the capture
        // validation sees the grade as belonging to the entry's farm.
        var flock = await client.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = "F", breed = "B", placementDate = "2026-01-01", initialCount = 10 });
        Assert.Equal(HttpStatusCode.Created, flock.StatusCode);
        var flockBody = await flock.Content.ReadFromJsonAsync<IdDto>();
        var flocks = await client.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks");
        var f = flocks!.Single(x => x.Id == flockBody!.Id);

        var grade = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Bantam", gradeType = "Size", sortOrder = 1, isSaleable = true });
        var gradeId = (await grade.Content.ReadFromJsonAsync<IdDto>())!.Id;

        object EntryBody(string date) => new
        {
            farmId = f.FarmId,
            houseId = f.HouseId,
            flockId = f.Id,
            date,
            totalEggs = 10,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = gradeId, quantity = 5 } },
        };

        // Active grade: capture accepts it.
        var ok = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody("2026-07-14"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        await client.PostWithKeyAsync($"/api/v1/egg-grades/{gradeId}/deactivate", Guid.NewGuid().ToString());

        // Deactivated grade: capture rejects the line (handler-level unknown
        // grade -> 422, same as any other invalid grade reference).
        var rejected = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), EntryBody("2026-07-15"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
    }

    [Fact]
    public async Task Grade_ParallelUpdates_NoTornWrite()
    {
        // Version-token race per the aggregate-mutation rule: two concurrent
        // updates must not interleave — the final row is exactly one request's
        // full payload (the loser either 409s or is cleanly serialized after).
        var (client, accountId) = await SetupAsync();
        var create = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Race", gradeType = "Custom", sortOrder = 0, isSaleable = true });
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var a = PutWithKeyAsync(client, $"/api/v1/egg-grades/{id}",
            new { name = "Race A", sortOrder = 1, isSaleable = true });
        var b = PutWithKeyAsync(client, $"/api/v1/egg-grades/{id}",
            new { name = "Race B", sortOrder = 2, isSaleable = false });
        var responses = await Task.WhenAll(a, b);

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
            $"unexpected {(int)r.StatusCode}"));
        var successes = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.True(successes >= 1);

        var all = await client.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades?includeInactive=true");
        var final = all!.Single(g => g.Id == id);
        var isA = final is { Name: "Race A", SortOrder: 1, IsSaleable: true };
        var isB = final is { Name: "Race B", SortOrder: 2, IsSaleable: false };
        Assert.True(isA || isB, $"torn write: {final.Name}/{final.SortOrder}/{final.IsSaleable}");

        // The full-payload check above passes even without the Version token
        // (each UPDATE writes all three fields), so also pin the token itself:
        // every 204 must have advanced Version by exactly one.
        var version = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggGrades.FirstAsync(g => g.Id == id)).Version);
        Assert.Equal(successes, version);
    }

    [Fact]
    public async Task Grade_ParallelCreates_SameName_ExactlyOneWins()
    {
        // Two concurrent creates can both pass the handler's friendly pre-check;
        // the lower(Name) unique index must reject the loser (global 409 mapping).
        var client = await SetupClientAsync();

        var a = client.PostWithKeyAsync("/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Duplo", gradeType = "Custom", sortOrder = 1, isSaleable = true });
        var b = client.PostWithKeyAsync("/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "duplo", gradeType = "Custom", sortOrder = 2, isSaleable = true });
        var responses = await Task.WhenAll(a, b);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var all = await client.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades?includeInactive=true");
        Assert.Single(all!, g => g.Name.Equals("duplo", StringComparison.OrdinalIgnoreCase));
    }

    // #494 — creation wasn't on the audit trail at all; only corrections were.
    [Fact]
    public async Task Grade_Create_WritesAuditEvent()
    {
        var (client, accountId) = await SetupAsync();

        var create = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Jumbo", gradeType = "Custom", sortOrder = 7, isSaleable = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "EggGrade" && e.EntityId == id)
            .ToListAsync());

        var created = Assert.Single(events);
        Assert.Equal("EggGrade.Create", created.Action);
    }

    private sealed record FlockDto(Guid Id, Guid FarmId, Guid HouseId, string Name);
}
