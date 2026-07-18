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
        decimal? MeterStart, decimal? MeterEnd, string? Note);

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
    }

    [Fact]
    public async Task Update_Corrects_AndKeepsFlockDate()
    {
        var (client, accountId, flockId) = await SetupAsync();
        var created = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 100m, source = "Well" });
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        var update = await PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
            new { quantity = 90m, unit = "L", source = "Tank", note = "recount" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

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

    // AGENTS.md Version-token rule: parallel corrections of the same record
    // must not silently merge — exactly one wins, the loser 409s.
    [Fact]
    public async Task ParallelUpdates_ExactlyOneWins()
    {
        var (client, accountId, flockId) = await SetupAsync();
        var created = await client.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 100m, source = "Well" });
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        var versionBefore = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.WaterUsages.FirstAsync(u => u.Id == id)).Version);

        var responses = await Task.WhenAll(
            PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
                new { quantity = 80m, source = "Well" }),
            PutWithKeyAsync(client, $"/api/v1/water-usage/{id}",
                new { quantity = 70m, source = "Tank" }));

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
}
