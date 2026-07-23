namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// #35 end-to-end. The FarmClock unit tests prove the conversion; these prove the
// STOCK READ and the SALE actually use it — reverting either call site to
// clock.TodayUtc leaves the unit tests green but fails these.
//
// The scenario is the issue's own: 18:00 on July 15 in Los Angeles is already
// July 16 in UTC, so a lot restricted through the 15th — eggs still inside a
// medication withdrawal period — would read as available a day early on the UTC
// boundary, and a sale would be allowed to draw on it.
[Collection(IntegrationCollection.Name)]
public sealed class FarmLocalRestrictionTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateTime UtcInstant = new(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc);
    private const string FarmZone = "America/Los_Angeles";
    private static readonly DateOnly RestrictedThrough = new(2026, 7, 15);

    private sealed class FrozenClock : IClock
    {
        public DateTime UtcNow => UtcInstant;
        public DateOnly TodayUtc => DateOnly.FromDateTime(UtcInstant);
        public DateOnly TodayInZone(string timeZoneId) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                UtcInstant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
    }

    // Only the app's own date logic is frozen. The token is minted by the base
    // factory on the real clock, so JWT validation (which uses system time, not
    // IClock) still accepts it.
    private HttpClient FrozenClient(string accessToken)
    {
        var frozen = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddScoped<IClock, FrozenClock>()));
        var client = frozen.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    [Fact]
    public async Task LotRestrictedThroughFarmToday_IsNotSellable_EvenThoughUtcHasRolledOver()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email, timeZoneId: FarmZone);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");

        await factory.SeedEggLotAsync(
            accountId, grades["Large"], 100,
            restrictedUntil: RestrictedThrough,
            productionDate: new DateOnly(2026, 7, 1));
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);

        var client = FrozenClient(await factory.LoginForAccessTokenAsync(email));

        // Farm-local today is still the 15th, so the lot is restricted, not
        // available. On the UTC boundary (the 16th) this would read available.
        var stock = await client.GetFromJsonAsync<List<StockRow>>("/api/v1/stock");
        var large = stock!.Single(r => r.EggGradeId == grades["Large"]);
        Assert.Equal(0, large.Available);
        Assert.Equal(100, large.Restricted);

        // And the allocation agrees — the sale cannot draw on it.
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
    }

    [Fact]
    public async Task OnceFarmLocalPassesTheRestriction_TheLotSells()
    {
        // The mirror: same lot, restricted only through the 14th, so the farm's
        // own 15th has cleared it. Guards against "fixing" the boundary by
        // simply refusing everything.
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email, timeZoneId: FarmZone);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");

        await factory.SeedEggLotAsync(
            accountId, grades["Large"], 100,
            restrictedUntil: new DateOnly(2026, 7, 14),
            productionDate: new DateOnly(2026, 7, 1));
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);

        var client = FrozenClient(await factory.LoginForAccessTokenAsync(email));

        var stock = await client.GetFromJsonAsync<List<StockRow>>("/api/v1/stock");
        var large = stock!.Single(r => r.EggGradeId == grades["Large"]);
        Assert.Equal(100, large.Available);

        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    private sealed record StockRow(Guid EggGradeId, string GradeName, int Available, int Restricted);
}
