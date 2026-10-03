using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Eggs;
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
//
// The seeds keep any other row order from matching the canonical one by luck:
// the later lot is inserted first and holds fewer eggs, and on different dates
// it also has the smaller Id. Heap order, IX_EggLots_Allocation's quantity order
// and primary-key order then disagree with the canonical order wherever they can.
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
        Assert.Equal((0, 20), await AvailableAsync(accountId, earlierLot, laterLot));

        await AssertLockedInOrderAsync(accountId, earlierLot, laterLot,
            () => client.PostWithKeyAsync($"/api/v1/sales/{order}/void", Guid.NewGuid().ToString(),
                new { reason = "Confirmed by mistake" }));
    }

    // The first line's grade holds the later lot. The one farm-wide statement
    // still locks the other grade's earlier lot first, where a lock per grade
    // in line order would park on the first grade and never reach it (#854).
    // Each line is its own request, so the lines keep the order they were added in.
    [Fact]
    public async Task ConfirmSale_TwoGrades_HoldsTheOtherGradesEarlierLotWhileWaitingOnTheLater()
    {
        var farmId = Guid.NewGuid();
        var (client, accountId, grades) = await SetupAsync(farmId, "Large", "Medium");
        var (low, high) = await IdsInDatabaseOrderAsync(accountId);
        var (earlierLot, laterLot) = (high, low);
        await factory.SeedEggLotAsync(accountId, grades["Large"], 30, productionDate: Today, lotId: laterLot);
        await factory.SeedEggLotAsync(accountId, grades["Medium"], 40,
            productionDate: Today.AddDays(-5), lotId: earlierLot);
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Two Grade Buyer", phone = "555-0100" });
        var created = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id, orderDate = Today });
        var order = (await created.Content.ReadFromJsonAsync<Created>())!.Id;
        foreach (var grade in new[] { "Large", "Medium" })
        {
            var productId = await factory.SeedProductAsync(accountId, farmId, grades[grade], defaultPriceMinorUnits: 100);
            (await client.PostWithKeyAsync($"/api/v1/sales/{order}/items", Guid.NewGuid().ToString(),
                new { productId, quantity = 10 })).EnsureSuccessStatusCode();
        }
        Assert.Equal([grades["Large"], grades["Medium"]], await LineGradesAsync(accountId, order));

        await AssertLockedInOrderAsync(accountId, earlierLot, laterLot,
            () => client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()));
    }

    // One entry's lots share a production date, so Id alone orders them. The
    // entry and its lots are seeded directly so the test picks their Ids.
    [Fact]
    public async Task VoidDailyEntry_HoldsTheEarlierLotWhileWaitingOnTheLater()
    {
        var farmId = Guid.NewGuid();
        var (client, accountId, grades) = await SetupAsync(farmId, "Large", "Medium");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var entryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var entry = DailyEntry.Create(entryId, accountId, farmId, Guid.NewGuid(), flockId, Today);
            Assert.True(entry.RecordProduction(30, 0, 0, 0, 0,
                [new GradeQuantity(grades["Large"], 10), new GradeQuantity(grades["Medium"], 20)]).IsSuccess);
            Assert.True(entry.Submit().IsSuccess);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
        });
        var (earlierLot, laterLot) = await IdsInDatabaseOrderAsync(accountId);
        await factory.SeedEggLotAsync(accountId, grades["Medium"], 20,
            productionDate: Today, lotId: laterLot, dailyEntryId: entryId);
        await factory.SeedEggLotAsync(accountId, grades["Large"], 10,
            productionDate: Today, lotId: earlierLot, dailyEntryId: entryId);
        var version = (await client.GetFromJsonAsync<EntryVersion>($"/api/v1/daily-entries/{entryId}"))!.Version;

        await AssertLockedInOrderAsync(accountId, earlierLot, laterLot,
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

    // A 40-egg earlier lot and a 30-egg later lot, so a 50-egg sale draws from
    // both. The later lot is inserted first.
    private async Task<(Guid Earlier, Guid Later)> SeedTwoLotsAsync(Guid accountId, Guid gradeId, int daysApart)
    {
        var (low, high) = await IdsInDatabaseOrderAsync(accountId);
        var (earlierLot, laterLot) = daysApart == 0 ? (low, high) : (high, low);
        await factory.SeedEggLotAsync(accountId, gradeId, 30,
            productionDate: Today.AddDays(daysApart - 5), lotId: laterLot);
        await factory.SeedEggLotAsync(accountId, gradeId, 40,
            productionDate: Today.AddDays(-5), lotId: earlierLot);

        var canonical = await factory.WithTenantScopeAsync(accountId, db => db.EggLots
            .Where(l => l.Id == earlierLot || l.Id == laterLot)
            .OrderBy(l => l.ProductionDate).ThenBy(l => l.Id)
            .Select(l => l.Id)
            .ToListAsync());
        Assert.Equal([earlierLot, laterLot], canonical);
        return (earlierLot, laterLot);
    }

    // Two fresh Ids, lower first in PostgreSQL's uuid order: the test asks the
    // database that applies the lock order rather than a C# comparison.
    private Task<(Guid Low, Guid High)> IdsInDatabaseOrderAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
            var aFirst = await db.Database.SqlQuery<bool>($"""SELECT {a} < {b} AS "Value" """).SingleAsync();
            return aFirst ? (a, b) : (b, a);
        });

    // The order a confirm reads an order's lines in.
    private Task<List<Guid>> LineGradesAsync(Guid accountId, Guid orderId) =>
        factory.WithTenantScopeAsync(accountId, db => db.SalesOrderItems
            .Where(i => i.SalesOrderId == orderId)
            .OrderBy(i => i.CreatedAtUtc).ThenBy(i => EF.Property<long>(i, "Sequence"))
            .Select(i => i.EggGradeId)
            .ToListAsync());

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
            new DbContextOptionsBuilder<AppDbContext>().ConfigureWarnings(QueryShapeWarnings.Configure).UseNpgsql(factory.ConnectionString).Options,
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
