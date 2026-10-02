using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

// Every FOR UPDATE read over EggLots locks in (ProductionDate, Id) order, so a
// confirm, a sale void and a daily-entry void that touch the same lots queue
// behind each other instead of deadlocking. Each test holds the canonically
// LATER lot, parks the request on it, and then finds the EARLIER lot already
// locked by that request. Before #853 nothing failed when either order flipped.
[Collection(IntegrationCollection.Name)]
public sealed class EggLotLockOrderTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record EntryVersion(int Version);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    // daysApart 0 puts both lots on one production date, where Id alone orders them.
    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public async Task ConfirmSale_HoldsTheEarlierLotWhileWaitingOnTheLater(int daysApart)
    {
        var (client, accountId, grades) = await SetupAsync(Guid.NewGuid(), "Large");
        var (earlierLot, laterLot) = await SeedTwoLotsAsync(accountId, grades["Large"], daysApart);
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 50);

        await AssertLockedInOrderAsync(accountId, earlierLot, laterLot,
            () => client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public async Task VoidSale_HoldsTheEarlierSourceLotWhileWaitingOnTheLater(int daysApart)
    {
        var (client, accountId, grades) = await SetupAsync(Guid.NewGuid(), "Large");
        var (earlierLot, laterLot) = await SeedTwoLotsAsync(accountId, grades["Large"], daysApart);
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 50);
        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();
        Assert.Equal((0, 10), await AvailableAsync(accountId, earlierLot, laterLot));

        await AssertLockedInOrderAsync(accountId, earlierLot, laterLot,
            () => client.PostWithKeyAsync($"/api/v1/sales/{order}/void", Guid.NewGuid().ToString(),
                new { reason = "Confirmed by mistake" }));
    }

    // One entry's lots share a production date, so Id alone orders them.
    [Fact]
    public async Task VoidDailyEntry_HoldsTheEarlierLotWhileWaitingOnTheLater()
    {
        var farmId = Guid.NewGuid();
        var (client, accountId, grades) = await SetupAsync(farmId, "Large", "Medium");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = Today,
            totalEggs = 30, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            grades = new[]
            {
                new { eggGradeId = grades["Large"], quantity = 10 },
                new { eggGradeId = grades["Medium"], quantity = 20 },
            },
        });
        Assert.Equal(HttpStatusCode.Created, record.StatusCode);
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        (await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();
        var version = (await client.GetFromJsonAsync<EntryVersion>($"/api/v1/daily-entries/{entryId}"))!.Version;

        var lots = await factory.WithTenantScopeAsync(accountId, db => db.EggLots
            .Where(l => l.DailyEntryId == entryId)
            .OrderBy(l => l.ProductionDate).ThenBy(l => l.Id)
            .Select(l => l.Id)
            .ToListAsync());
        Assert.Equal(2, lots.Count);

        await AssertLockedInOrderAsync(accountId, lots[0], lots[1],
            () => client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/void", Guid.NewGuid().ToString(),
                new { version, reason = "Recorded against the wrong flock" }));
    }

    private async Task AssertLockedInOrderAsync(
        Guid accountId, Guid earlierLot, Guid laterLot, Func<Task<HttpResponseMessage>> send)
    {
        HttpResponseMessage response;
        await using (var holder = await HoldAsync(accountId, laterLot))
        {
            var request = send();
            Assert.True(await factory.WaitUntilDoneOrBlockedAsync(request, holder.Pid),
                "the request must park on the later lot");

            Assert.True(await IsLockedElsewhereAsync(accountId, earlierLot),
                "the request must already hold the earlier lot while it waits on the later one");

            await holder.Transaction.CommitAsync();
            response = await request;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Two 30-egg lots, so a 50-egg sale draws from both. PostgreSQL decides
    // which is earlier: its uuid order differs from System.Guid's.
    private async Task<(Guid Earlier, Guid Later)> SeedTwoLotsAsync(Guid accountId, Guid gradeId, int daysApart)
    {
        var first = await factory.SeedEggLotAsync(accountId, gradeId, 30, productionDate: Today.AddDays(-5));
        var second = await factory.SeedEggLotAsync(accountId, gradeId, 30, productionDate: Today.AddDays(daysApart - 5));
        var ordered = await factory.WithTenantScopeAsync(accountId, db => db.EggLots
            .Where(l => l.Id == first || l.Id == second)
            .OrderBy(l => l.ProductionDate).ThenBy(l => l.Id)
            .Select(l => l.Id)
            .ToListAsync());
        return (ordered[0], ordered[1]);
    }

    private Task<(int Earlier, int Later)> AvailableAsync(Guid accountId, Guid earlierLot, Guid laterLot) =>
        factory.WithTenantScopeAsync(accountId, async db => (
            (await db.EggLots.SingleAsync(l => l.Id == earlierLot)).QuantityAvailable,
            (await db.EggLots.SingleAsync(l => l.Id == laterLot)).QuantityAvailable));

    private async Task<bool> IsLockedElsewhereAsync(Guid accountId, Guid lotId)
    {
        await using var db = NewContext(accountId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "EggLots" WHERE "Id" = {lotId} FOR UPDATE NOWAIT""");
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return true;
        }
    }

    private async Task<(HttpClient Client, Guid AccountId, Dictionary<string, Guid> Grades)> SetupAsync(
        Guid farmId, params string[] gradeNames)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, gradeNames);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, grades);
    }

    // Built directly rather than from factory.Services: that context retries on
    // failure, which forbids a hand-begun transaction (#269).
    private AppDbContext NewContext(Guid accountId)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        return new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
    }

    private async Task<Holder> HoldAsync(Guid accountId, Guid lotId)
    {
        var db = NewContext(accountId);
        var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "EggLots" WHERE "Id" = {lotId} FOR UPDATE""");
        return new Holder(db, transaction, await db.BackendPidAsync());
    }

    private sealed record Holder(AppDbContext Db, IDbContextTransaction Transaction, int Pid) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Db.DisposeAsync();
        }
    }
}
