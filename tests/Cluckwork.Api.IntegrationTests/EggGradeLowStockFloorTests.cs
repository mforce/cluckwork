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
    public async Task An_active_floored_grade_with_no_lots_still_warns()
    {
        // The case the feature exists for: a grade the farm has run out of, or
        // has never produced, has no lot rows at all. Aggregating lots alone
        // returns no row for it, so nothing warns precisely when stock is zero.
        var (owner, _, accountId) = await SetupAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, SeedDefaults.FarmId, "Empty");

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(owner, grades["Empty"],
            new { name = "Empty", sortOrder = 0, isSaleable = true, lowStockFloor = 500 })).StatusCode);

        var stock = await owner.GetFromJsonAsync<List<StockRow>>("/api/v1/stock");

        var row = Assert.Single(stock!, r => r.EggGradeId == grades["Empty"]);
        Assert.Equal(0, row.Available);
        Assert.Equal(0, row.Restricted);
        Assert.Equal(500, row.LowStockFloor);
        Assert.True(row.BelowFloor);
    }

    [Fact]
    public async Task A_grade_with_neither_stock_nor_a_floor_stays_off_the_board()
    {
        // The complement, so the fix above cannot be "list every grade": a
        // grade with nothing to say stays off Stock exactly as before.
        var (owner, _, accountId) = await SetupAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, SeedDefaults.FarmId, "Quiet");

        var stock = await owner.GetFromJsonAsync<List<StockRow>>("/api/v1/stock");

        Assert.DoesNotContain(stock!, r => r.EggGradeId == grades["Quiet"]);
    }

    [Fact]
    public async Task Deactivating_a_grade_takes_its_floor_out_of_service()
    {
        // Deactivation removes a grade from capture and order pickers, so its
        // floor stops being a thing the farm can act on. The stock the grade
        // still holds stays on the board; the warning does not.
        var (owner, _, accountId) = await SetupAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, SeedDefaults.FarmId, "Retiring");
        await factory.SeedEggLotAsync(accountId, grades["Retiring"], 100);
        var id = grades["Retiring"];

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(owner, id,
            new { name = "Retiring", sortOrder = 0, isSaleable = true, lowStockFloor = 5000 })).StatusCode);
        var active = (await owner.GetFromJsonAsync<List<StockRow>>("/api/v1/stock"))!.Single(r => r.EggGradeId == id);
        Assert.True(active.BelowFloor);

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostWithKeyAsync($"/api/v1/egg-grades/{id}/deactivate", Guid.NewGuid().ToString())).StatusCode);

        var inactive = (await owner.GetFromJsonAsync<List<StockRow>>("/api/v1/stock"))!.Single(r => r.EggGradeId == id);
        Assert.Equal(100, inactive.Available);
        Assert.Null(inactive.LowStockFloor);
        Assert.False(inactive.BelowFloor);

        // Reactivating puts the stored floor back in service unchanged.
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostWithKeyAsync($"/api/v1/egg-grades/{id}/activate", Guid.NewGuid().ToString())).StatusCode);

        var reactivated = (await owner.GetFromJsonAsync<List<StockRow>>("/api/v1/stock"))!.Single(r => r.EggGradeId == id);
        Assert.Equal(5000, reactivated.LowStockFloor);
        Assert.True(reactivated.BelowFloor);
    }

    [Fact]
    public async Task An_inactive_floored_grade_with_no_lots_is_off_the_board_entirely()
    {
        // The two fixes meet here: an empty grade earns a row from its floor,
        // and an inactive grade has no floor in service, so it earns nothing.
        var (owner, _, accountId) = await SetupAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, SeedDefaults.FarmId, "Gone");
        var id = grades["Gone"];

        await PutAsync(owner, id, new { name = "Gone", sortOrder = 0, isSaleable = true, lowStockFloor = 500 });
        await owner.PostWithKeyAsync($"/api/v1/egg-grades/{id}/deactivate", Guid.NewGuid().ToString());

        var stock = await owner.GetFromJsonAsync<List<StockRow>>("/api/v1/stock");

        Assert.DoesNotContain(stock!, r => r.EggGradeId == id);
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

    // The deterministic one-winner assertion lives in EggGradeFloorRaceTests
    // (#950 review round 1): it needs a rendezvous interceptor, which needs its
    // own host, and a test that merely races two requests cannot tell a working
    // concurrency token from two writes that happened to serialize.
}
