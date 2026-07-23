namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #155 — the boundaries #35 left on UTC, now on the farm clock. Every case here
// is a farm AHEAD of UTC, which is the direction that used to REFUSE legitimate
// work: at 10:00 on July 16 in Auckland it is still July 15 in UTC, so the
// farm's own today looked like tomorrow and was rejected as "in the future".
//
// These would all pass against the old UTC code if the farm were on UTC, so the
// non-UTC timezone is the whole point — reverting any converted site to
// clock.TodayUtc / DateTime.UtcNow fails the matching case here.
[Collection(IntegrationCollection.Name)]
public sealed class FarmLocalBoundarySweepTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateTime UtcInstant = new(2026, 7, 15, 22, 0, 0, DateTimeKind.Utc);
    private const string FarmZone = "Pacific/Auckland";
    private static readonly DateOnly FarmToday = new(2026, 7, 16);   // UTC still says the 15th
    private static readonly DateOnly UtcToday = new(2026, 7, 15);

    private sealed class FrozenClock : IClock
    {
        public DateTime UtcNow => UtcInstant;
        public DateOnly TodayUtc => DateOnly.FromDateTime(UtcInstant);
        public DateOnly TodayInZone(string timeZoneId) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                UtcInstant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
    }

    // Only the app's date logic is frozen; the token is minted on the real clock
    // by the base factory, so JWT validation still accepts it.
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

    private sealed record IdDto(Guid Id);

    private static Task<HttpResponseMessage> CreateFlockAsync(HttpClient client, DateOnly placedOn) =>
        client.PostWithKeyAsync("/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = $"Barn {Guid.NewGuid():N}"[..12], breed = "ISA Brown", placementDate = placedOn, initialCount = 200 });

    // CreateFlockValidator
    [Fact]
    public async Task PlacementDateOfFarmToday_IsAccepted_ThoughUtcCallsItTomorrow()
    {
        Assert.True(FarmToday > UtcToday, "the fixture must straddle the date line for this to mean anything");
        var (client, _) = await FrozenFarmAsync();

        var created = await CreateFlockAsync(client, FarmToday);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task PlacementDateAfterFarmToday_IsStillRefused()
    {
        // The rule still bites — it just moved to the right calendar.
        var (client, _) = await FrozenFarmAsync();

        var created = await CreateFlockAsync(client, FarmToday.AddDays(1));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    // DepleteFlockHandler — this date is STORED, so a UTC one persists the wrong
    // day and then decides which backfill the flock accepts.
    [Fact]
    public async Task Deplete_StampsTheFarmsDate_NotUtcs()
    {
        var (client, accountId) = await FrozenFarmAsync();
        var created = await CreateFlockAsync(client, new DateOnly(2026, 1, 1));
        var flockId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var depleted = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{flockId}/deplete", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, depleted.StatusCode);

        var stamped = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.Flocks.AsNoTracking().SingleAsync(f => f.Id == flockId)).DepletedOn);
        Assert.Equal(FarmToday, stamped);
    }

    // ReportEndpoints — the default window and the future guard.
    [Fact]
    public async Task Report_ToFarmToday_IsAccepted_NotRejectedAsFuture()
    {
        var (client, _) = await FrozenFarmAsync();

        var report = await client.GetAsync(
            $"/api/v1/reports/production?from={FarmToday.AddDays(-6):yyyy-MM-dd}&to={FarmToday:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
    }

    [Fact]
    public async Task Report_ToAfterFarmToday_IsStillRejected()
    {
        var (client, _) = await FrozenFarmAsync();

        var report = await client.GetAsync(
            $"/api/v1/reports/production?to={FarmToday.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.BadRequest, report.StatusCode);
    }
}
