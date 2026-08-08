namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #67 — water usage: direct or meter-derived quantities, editable records
// (Version-guarded), same flock lifecycle gate as production/feed.
[Collection(IntegrationCollection.Name)]
public sealed class WaterUsageTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record Row(
        Guid Id, Guid FlockId, DateOnly Date, decimal Quantity, string Unit, string Source,
        decimal? MeterStart, decimal? MeterEnd, string? Note, int Version);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Client, Guid AccountId, Guid FlockId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var flockId = await factory.SeedFlockAsync(accountId, Guid.NewGuid());
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, flockId);
    }

    private static Task<HttpResponseMessage> PutWithKeyAsync(HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Record_DirectAndMeterDerived_ListWithFilters()
    {
        var (client, _, flockId) = await SetupAsync();

        var direct = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 120.5m, source = "Well", note = "morning" });
        Assert.Equal(HttpStatusCode.Created, direct.StatusCode);

        // Meter-derived: quantity omitted, comes from the delta.
        var metered = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today.AddDays(-1), source = "Municipal", meterStart = 1000m, meterEnd = 1085.25m });
        Assert.Equal(HttpStatusCode.Created, metered.StatusCode);

        var rows = await client.GetFromJsonAsync<List<Row>>($"/api/v1/water-usage?flockId={flockId}");
        Assert.Equal(2, rows!.Count);
        Assert.Contains(rows, r => r.Quantity == 120.5m && r.Source == "Well" && r.MeterStart == null);
        Assert.Contains(rows, r => r.Quantity == 85.25m && r.MeterStart == 1000m && r.MeterEnd == 1085.25m);

        var filtered = await client.GetFromJsonAsync<List<Row>>(
            $"/api/v1/water-usage?flockId={flockId}&from={Today:yyyy-MM-dd}");
        Assert.Single(filtered!);
    }

    [Fact]
    public async Task Record_Guards()
    {
        var (client, accountId, flockId) = await SetupAsync();

        // Quantity disagreeing with the meter delta → 400.
        var mismatch = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 50m, source = "Well", meterStart = 100m, meterEnd = 110m });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        // Neither quantity nor meters → 400.
        var neither = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, source = "Well" });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        // Meter end before start → 400.
        var backwards = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, source = "Well", meterStart = 110m, meterEnd = 100m });
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        // Unknown source → 400; bad unit → 400.
        var badSource = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m, source = "River" });
        Assert.Equal(HttpStatusCode.BadRequest, badSource.StatusCode);
        var badUnit = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m, unit = "m3", source = "Well" });
        Assert.Equal(HttpStatusCode.BadRequest, badUnit.StatusCode);

        // Future date → 422; archived flock → 422.
        var future = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today.AddDays(2), quantity = 10m, source = "Well" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = await db.Flocks.FirstAsync(f => f.Id == flockId);
            flock.Deplete(Today.AddDays(-1));
            flock.Archive(Today);
            await db.SaveChangesAsync();
        });
        var archived = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m, source = "Well" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, archived.StatusCode);

        // Numeric enum strings are refused — named values only.
        var numericEnum = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m, source = "1" });
        Assert.Equal(HttpStatusCode.BadRequest, numericEnum.StatusCode);

        // Meter readings beyond 3 decimals would round into Quantity ≠ delta
        // after numeric(18,3) persistence.
        var precise = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, source = "Well", meterStart = 1.0001m, meterEnd = 2.0001m });
        Assert.Equal(HttpStatusCode.BadRequest, precise.StatusCode);
    }

    [Fact]
    public async Task Update_ArchivedFlockRecord_IsReadOnly()
    {
        var (client, accountId, flockId) = await SetupAsync();
        var created = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 50m, source = "Well" });
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = await db.Flocks.FirstAsync(f => f.Id == flockId);
            flock.Deplete(Today);
            flock.Archive(Today);
            await db.SaveChangesAsync();
        });

        var update = await PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
            new { version = 0, quantity = 60m, source = "Well" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
    }

    [Fact]
    public async Task Update_Corrects_AndKeepsFlockDate()
    {
        var (client, accountId, flockId) = await SetupAsync();
        var created = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 100m, source = "Well" });
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        var update = await PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
            new { version = 0, quantity = 90m, unit = "L", source = "Tank", note = "recount" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        // Replaying the ORIGINAL base version after the edit is a stale form
        // — deterministic 409, nothing changes.
        var stale = await PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
            new { version = 0, quantity = 999m, source = "Well" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var rows = await client.GetFromJsonAsync<List<Row>>($"/api/v1/water-usage?flockId={flockId}");
        var row = Assert.Single(rows!);
        Assert.Equal(90m, row.Quantity);
        Assert.Equal("Tank", row.Source);
        Assert.Equal("recount", row.Note);
        Assert.Equal(Today, row.Date);

        var version = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.WaterUsages.FirstAsync(u => u.Id == id)).Version);
        Assert.Equal(1, version);
    }

    // The full optimistic contract (codex review of PR #76): both writers send
    // the SAME base version, so exactly one wins DETERMINISTICALLY — no timing
    // window. The earlier token-only design let both 204 when the requests
    // serialized outside the handler's read→save gap.
    [Fact]
    public async Task ParallelUpdates_SameBaseVersion_ExactlyOneWins()
    {
        var (client, accountId, flockId) = await SetupAsync();
        var created = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 100m, source = "Well" });
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        var versionBefore = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.WaterUsages.FirstAsync(u => u.Id == id)).Version);

        var responses = await Task.WhenAll(
            PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
                new { version = versionBefore, quantity = 80m, source = "Well" }),
            PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
                new { version = versionBefore, quantity = 70m, source = "Tank" }));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var (versionAfter, quantity, source) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var row = await db.WaterUsages.FirstAsync(u => u.Id == id);
            return (row.Version, row.Quantity, row.Source.ToString());
        });
        Assert.Equal(versionBefore + 1, versionAfter);
        // Whole-payload consistency — the winner's quantity AND source.
        Assert.True(
            (quantity == 80m && source == "Well") || (quantity == 70m && source == "Tank"),
            $"blended write: {quantity} / {source}");
    }

    // -----------------------------------------------------------------------
    // #446 — record-time DailyEntryId stamping, same contract as feed: link
    // the non-voided entry that exists at record time, never backfill. The
    // void/re-create nuances are pinned on the feed side (shared contract,
    // FeedUsageTests) — here the two base cases prove water's own handler
    // resolves the link too, and that Update never touches it.
    // -----------------------------------------------------------------------

    private sealed record RowWithEntry(Guid Id, string? Note, int Version, Guid? DailyEntryId);

    [Fact]
    public async Task Record_WithExistingDailyEntry_StampsTheLink_AndUpdateKeepsIt()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId, houseId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId, flockId, date = Today,
            totalEggs = 0, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            grades = Array.Empty<object>(),
        });
        record.EnsureSuccessStatusCode();
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;

        var created = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 100m, source = "Well" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var row = Assert.Single((await client.GetFromJsonAsync<List<RowWithEntry>>(
            $"/api/v1/water-usage?flockId={flockId}"))!);
        Assert.Equal(entryId, row.DailyEntryId);

        // A correction keeps the link exactly as recorded.
        var put = await PutWithKeyAsync(client, $"/api/v1/water-usage/{row.Id}",
            new { quantity = 90m, unit = "L", source = "Well", note = "corrected", version = row.Version });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var corrected = Assert.Single((await client.GetFromJsonAsync<List<RowWithEntry>>(
            $"/api/v1/water-usage?flockId={flockId}"))!);
        Assert.Equal(entryId, corrected.DailyEntryId);
    }

    [Fact]
    public async Task Record_NoDailyEntry_LinkStaysNull()
    {
        var (client, _, flockId) = await SetupAsync();

        var created = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 100m, source = "Well" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var row = Assert.Single((await client.GetFromJsonAsync<List<RowWithEntry>>(
            $"/api/v1/water-usage?flockId={flockId}"))!);
        Assert.Null(row.DailyEntryId);
    }
}
