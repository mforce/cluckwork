namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #155, the other direction. FarmLocalBoundarySweepTests uses a farm AHEAD of
// UTC (Auckland), where the old code REFUSED legitimate work. Here the farm is
// BEHIND UTC (Los Angeles), where the old code did the opposite and ACCEPTED
// the farm's tomorrow: at 18:00 on July 15 there it is already July 16 in UTC,
// so `date > TodayUtc` let a July 16 row through on a farm that has not
// reached July 16.
//
// Posting UTC-today therefore discriminates cleanly — it is the farm's
// tomorrow, so it must now be refused, and each handler is asserted on its own
// FutureDate code so an unrelated rejection (no stock, bad lot) cannot pass for
// the boundary working.
[Collection(IntegrationCollection.Name)]
public sealed class FarmLocalBoundaryBehindUtcTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateTime UtcInstant = new(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc);
    private const string FarmZone = "America/Los_Angeles";
    private static readonly DateOnly FarmToday = new(2026, 7, 15);
    private static readonly DateOnly FarmTomorrow = new(2026, 7, 16);   // == UTC today

    private sealed class FrozenClock : IClock
    {
        public DateTime UtcNow => UtcInstant;
        public DateOnly TodayUtc => DateOnly.FromDateTime(UtcInstant);
        public DateOnly TodayInZone(string timeZoneId) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                UtcInstant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
    }

    private sealed record IdDto(Guid Id);
    private sealed record ProblemDto(string? Title);
    private sealed record ProductionDayDto(DateOnly Date);
    private sealed record ProductionReportDto(List<ProductionDayDto> Days);

    private async Task<(HttpClient Client, Guid AccountId)> FrozenFarmAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email, timeZoneId: FarmZone);
        var token = await factory.LoginForAccessTokenAsync(email);

        var frozen = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddScoped<IClock, FrozenClock>()));
        var client = frozen.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (client, accountId);
    }

    private static async Task<Guid> CreateItemAsync(HttpClient client)
    {
        var created = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = $"Feed {Guid.NewGuid():N}"[..14], category = "Feed", unit = "kg", defaultUnitCostMinorUnits = 100L });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private static async Task<Guid> CreateFlockAsync(HttpClient client)
    {
        var created = await client.PostWithKeyAsync("/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = $"Barn {Guid.NewGuid():N}"[..12], breed = "ISA Brown", placementDate = "2026-01-01", initialCount = 200 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private static async Task AssertRefusedAsAsync(HttpResponseMessage response, string expectedCode)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal(expectedCode, problem!.Title);
    }

    // RecordPurchaseHandler
    [Fact]
    public async Task Purchase_DatedTheFarmsTomorrow_IsRefused_ThoughUtcCallsItToday()
    {
        var (client, _) = await FrozenFarmAsync();
        var itemId = await CreateItemAsync(client);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = FarmTomorrow, quantity = 10m, unitCostMinorUnits = 100L, lotNumber = (string?)null, expiryDate = (DateOnly?)null, note = (string?)null });

        await AssertRefusedAsAsync(response, "InventoryLot.FutureDate");
    }

    // RecordFeedUsageHandler
    [Fact]
    public async Task FeedUsage_DatedTheFarmsTomorrow_IsRefused()
    {
        var (client, _) = await FrozenFarmAsync();
        var itemId = await CreateItemAsync(client);
        var flockId = await CreateFlockAsync(client);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = FarmTomorrow, quantity = 1m, note = (string?)null });

        await AssertRefusedAsAsync(response, "FeedUsage.FutureDate");
    }

    // RecordAdjustmentHandler
    [Fact]
    public async Task StockAdjustment_DatedTheFarmsTomorrow_IsRefused()
    {
        var (client, _) = await FrozenFarmAsync();
        var itemId = await CreateItemAsync(client);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = Guid.NewGuid(), date = FarmTomorrow, type = "Adjustment", quantityDelta = 1m, reason = "recount" });

        await AssertRefusedAsAsync(response, "InventoryMovement.FutureDate");
    }

    // RecordWaterUsageHandler
    [Fact]
    public async Task WaterUsage_DatedTheFarmsTomorrow_IsRefused()
    {
        var (client, _) = await FrozenFarmAsync();
        var flockId = await CreateFlockAsync(client);

        var response = await client.PostWithKeyAsync(
            "/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = FarmTomorrow, quantity = 5m, unit = "L", source = "Municipal", meterStart = (decimal?)null, meterEnd = (decimal?)null, note = (string?)null });

        await AssertRefusedAsAsync(response, "WaterUsage.FutureDate");
    }

    // ArchiveFlockHandler — the second stored stamp (Deplete is covered in the
    // ahead-of-UTC suite).
    [Fact]
    public async Task Archive_StampsTheFarmsDate_NotUtcs()
    {
        var (client, accountId) = await FrozenFarmAsync();
        var flockId = await CreateFlockAsync(client);

        var archived = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/archive", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);

        var stamped = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.Flocks.AsNoTracking().SingleAsync(f => f.Id == flockId)).ArchivedOn);
        Assert.Equal(FarmToday, stamped);
        Assert.NotEqual(FarmTomorrow, stamped); // FarmTomorrow is what UTC would have stamped
    }

    // ReportEndpoints — the DEFAULT window (no query parameters), which the
    // future-guard tests don't reach.
    [Fact]
    public async Task ReportDefaultWindow_EndsOnTheFarmsToday_NotUtcs()
    {
        var (client, _) = await FrozenFarmAsync();

        var report = await client.GetFromJsonAsync<ProductionReportDto>("/api/v1/reports/production");

        // Default is the last 7 days inclusive, ending today — the farm's today.
        Assert.Equal(FarmToday, report!.Days[^1].Date);
        Assert.Equal(FarmToday.AddDays(-6), report.Days[0].Date);
    }
}
