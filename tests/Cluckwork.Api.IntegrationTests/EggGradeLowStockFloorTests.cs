namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

// #911 — the per-grade low-stock floor over the API: who may move it, what
// GET /stock reports about it, and the Version-token race the aggregate-mutation
// rule requires of a new mutation (AGENTS.md).
[Collection(IntegrationCollection.Name)]
public sealed class EggGradeLowStockFloorTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record GradeDto(Guid Id, string Name, int SortOrder, bool IsSaleable, int? LowStockFloor);
    private sealed record StockRow(
        Guid EggGradeId, string GradeName, int Available, int Restricted,
        int? LowStockFloor, bool BelowFloor);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-grades/{id}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private async Task<(HttpClient Owner, HttpClient Manager, Guid AccountId)> SetupAsync()
    {
        var ownerEmail = $"o-{Guid.NewGuid():N}@test.local";
        var managerEmail = $"m-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, managerEmail, Roles.Manager);
        return (
            factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail)),
            factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(managerEmail)),
            accountId);
    }

    private static async Task<Guid> CreateGradeAsync(HttpClient client, string name, int? floor = null)
    {
        var create = await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name, gradeType = "Size", sortOrder = 0, isSaleable = true, lowStockFloor = floor });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    [Fact]
    public async Task Owner_sets_reads_and_clears_a_floor()
    {
        var (owner, _, _) = await SetupAsync();
        var id = await CreateGradeAsync(owner, $"Floored {Guid.NewGuid():N}", floor: 5000);

        var created = await owner.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades");
        Assert.Equal(5000, created!.Single(g => g.Id == id).LowStockFloor);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(owner, id,
            new { name = "Floored", sortOrder = 0, isSaleable = true, lowStockFloor = 4000 })).StatusCode);
        var raised = await owner.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades");
        Assert.Equal(4000, raised!.Single(g => g.Id == id).LowStockFloor);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(owner, id,
            new { name = "Floored", sortOrder = 0, isSaleable = true })).StatusCode);
        var cleared = await owner.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades");
        Assert.Null(cleared!.Single(g => g.Id == id).LowStockFloor);
    }

    [Fact]
    public async Task A_negative_floor_is_rejected()
    {
        var (owner, _, _) = await SetupAsync();

        var create = await owner.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Negative", gradeType = "Size", sortOrder = 0, isSaleable = true, lowStockFloor = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task A_manager_keeps_the_grade_but_cannot_move_its_floor()
    {
        var (owner, manager, _) = await SetupAsync();
        var name = $"Managed {Guid.NewGuid():N}";
        var id = await CreateGradeAsync(owner, name, floor: 3000);

        // Every other field on the Grades dialog stays a Manager's to change,
        // as long as the floor it sends is the floor the grade already has.
        var rename = await PutAsync(manager, id,
            new { name = $"{name} renamed", sortOrder = 9, isSaleable = false, lowStockFloor = 3000 });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        var raise = await PutAsync(manager, id,
            new { name = $"{name} renamed", sortOrder = 9, isSaleable = false, lowStockFloor = 3500 });
        Assert.Equal(HttpStatusCode.Forbidden, raise.StatusCode);

        var clear = await PutAsync(manager, id,
            new { name = $"{name} renamed", sortOrder = 9, isSaleable = false });
        Assert.Equal(HttpStatusCode.Forbidden, clear.StatusCode);

        var create = await manager.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = $"Manager {Guid.NewGuid():N}", gradeType = "Size", sortOrder = 0, isSaleable = true, lowStockFloor = 100 });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var current = await owner.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades");
        var grade = current!.Single(g => g.Id == id);
        Assert.Equal(3000, grade.LowStockFloor);
        Assert.Equal(9, grade.SortOrder);
    }

    [Fact]
    public async Task Stock_reports_the_floor_and_the_below_floor_flag()
    {
        var (owner, _, accountId) = await SetupAsync();
        var farmId = SeedDefaults.FarmId;
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Above", "Below", "Unset");

        await factory.SeedEggLotAsync(accountId, grades["Above"], 6000);
        await factory.SeedEggLotAsync(accountId, grades["Below"], 4320);
        await factory.SeedEggLotAsync(accountId, grades["Unset"], 900);
        // The below-floor grade also holds restricted eggs that would lift it
        // over its floor if the flag counted them. It must not.
        await factory.SeedEggLotAsync(
            accountId, grades["Below"], 2000,
            restrictedUntil: DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(7));

        foreach (var (name, floor) in new[] { ("Above", 5000), ("Below", 5000) })
            Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(owner, grades[name],
                new { name, sortOrder = 0, isSaleable = true, lowStockFloor = floor })).StatusCode);

        var stock = await owner.GetFromJsonAsync<List<StockRow>>("/api/v1/stock");

        var above = stock!.Single(r => r.EggGradeId == grades["Above"]);
        Assert.Equal(5000, above.LowStockFloor);
        Assert.False(above.BelowFloor);

        var below = stock!.Single(r => r.EggGradeId == grades["Below"]);
        Assert.Equal(5000, below.LowStockFloor);
        Assert.Equal(4320, below.Available);
        Assert.Equal(2000, below.Restricted);
        Assert.True(below.BelowFloor);

        var unset = stock!.Single(r => r.EggGradeId == grades["Unset"]);
        Assert.Null(unset.LowStockFloor);
        Assert.False(unset.BelowFloor);
    }

    [Fact]
    public async Task A_floor_change_lands_on_the_grade_update_audit_action()
    {
        var (owner, _, accountId) = await SetupAsync();
        var id = await CreateGradeAsync(owner, $"Audited {Guid.NewGuid():N}");

        await PutAsync(owner, id,
            new { name = "Audited", sortOrder = 0, isSaleable = true, lowStockFloor = 1500 });

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "EggGrade" && e.EntityId == id && e.Action == "EggGrade.Update")
            .ToListAsync());

        var updated = Assert.Single(events);
        Assert.Contains("1500", updated.DetailsJson);
    }

    [Fact]
    public async Task Parallel_floor_updates_do_not_tear_the_grade()
    {
        var (owner, _, accountId) = await SetupAsync();
        var id = await CreateGradeAsync(owner, $"Race {Guid.NewGuid():N}");

        var a = PutAsync(owner, id, new { name = "Race A", sortOrder = 1, isSaleable = true, lowStockFloor = 1000 });
        var b = PutAsync(owner, id, new { name = "Race B", sortOrder = 2, isSaleable = false, lowStockFloor = 2000 });
        var responses = await Task.WhenAll(a, b);

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
            $"unexpected {(int)r.StatusCode}"));
        var successes = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.True(successes >= 1);

        var final = (await owner.GetFromJsonAsync<List<GradeDto>>("/api/v1/egg-grades"))!.Single(g => g.Id == id);
        var isA = final is { Name: "Race A", SortOrder: 1, IsSaleable: true, LowStockFloor: 1000 };
        var isB = final is { Name: "Race B", SortOrder: 2, IsSaleable: false, LowStockFloor: 2000 };
        Assert.True(isA || isB,
            $"torn write: {final.Name}/{final.SortOrder}/{final.IsSaleable}/{final.LowStockFloor}");

        var version = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggGrades.FirstAsync(g => g.Id == id)).Version);
        Assert.Equal(successes, version);
    }
}
