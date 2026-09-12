namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #7 / #9 / #12 — the read tier: daily-entry get/list, stock by grade, sales
// order get/list. All tenant-scoped via the global query filter.
[Collection(IntegrationCollection.Name)]
public sealed class ReadEndpointTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record GradeLineDto(Guid EggGradeId, int Quantity);
    private sealed record EntryDto(
        Guid Id, Guid FlockId, DateOnly Date, string Status, int TotalEggs, List<GradeLineDto> Grades);
    private sealed record StockDto(Guid EggGradeId, string GradeName, int Available, int Restricted);
    private sealed record OrderItemDto(Guid EggGradeId, int Quantity);
    private sealed record OrderDto(Guid Id, string Status, List<OrderItemDto> Items);
    // #769 — the settlement figure as the wire carries it.
    private sealed record OrderMoneyDto(
        Guid Id, string Status, long TotalMinorUnits, long? OutstandingMinorUnits);
    private sealed record PaymentRowDto(Guid Id, int Version);
    private sealed record PaymentsPageDto(List<PaymentRowDto> Items);

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Dictionary<string, Guid> Grades)>
        SetupAsync(params string[] gradeNames)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, gradeNames);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, grades);
    }

    private static object EntryBody(Guid farmId, Guid flockId, DateOnly date, object[] grades) => new
    {
        farmId,
        houseId = Guid.NewGuid(),
        flockId,
        date,
        totalEggs = 1000,
        crackedEggs = 10,
        dirtyEggs = 5,
        discardedEggs = 3,
        mortalityCount = 0,
        grades
    };

    [Fact]
    public async Task GetDailyEntry_ReturnsGradeLines()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var create = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, today, [new { eggGradeId = grades["Large"], quantity = 600 }]));
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var entry = await client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{id}");

        Assert.Equal(flockId, entry!.FlockId);
        Assert.Equal("Draft", entry.Status);
        Assert.Equal(1000, entry.TotalEggs);
        var line = Assert.Single(entry.Grades);
        Assert.Equal(grades["Large"], line.EggGradeId);
        Assert.Equal(600, line.Quantity);
    }

    [Fact]
    public async Task ListDailyEntries_FiltersByFlock_NewestFirst()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large");
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        foreach (var (flock, date) in new[] { (flockA, today.AddDays(-2)), (flockA, today), (flockB, today) })
            await client.PostWithKeyAsync(
                "/api/v1/daily-entries", Guid.NewGuid().ToString(),
                EntryBody(farmId, flock, date, [new { eggGradeId = grades["Large"], quantity = 100 }]));

        var list = await client.GetFromJsonAsync<List<EntryDto>>(
            $"/api/v1/daily-entries?flockId={flockA}");

        Assert.Equal(2, list!.Count);
        Assert.All(list, e => Assert.Equal(flockA, e.FlockId));
        Assert.True(list[0].Date >= list[1].Date);
    }

    [Fact]
    public async Task Stock_AggregatesByGrade_SeparatesRestricted()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large", "Medium");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Two submitted entries -> lots: Large 600 + 400, Medium 300. #394:
        // submit requires exact reconciliation, so each entry here is built
        // with zero losses and a total matching its own grade sum, rather
        // than through EntryBody's fixed (and here irrelevant) 1000/10/5/3.
        foreach (var (date, total, gradeQty) in new (DateOnly, int, object[])[]
        {
            (today.AddDays(-1), 900, [new { eggGradeId = grades["Large"], quantity = 600 },
                                 new { eggGradeId = grades["Medium"], quantity = 300 }]),
            (today, 400, [new { eggGradeId = grades["Large"], quantity = 400 }]),
        })
        {
            var create = await client.PostWithKeyAsync(
                "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
                {
                    farmId, houseId = Guid.NewGuid(), flockId, date,
                    totalEggs = total, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
                    mortalityCount = 0, grades = gradeQty
                });
            var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;
            await client.PostWithKeyAsync($"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());
        }

        // Plus a restricted lot of Large (withdrawal for another week).
        await factory.SeedEggLotAsync(accountId, grades["Large"], 50,
            DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(7));

        var stock = await client.GetFromJsonAsync<List<StockDto>>("/api/v1/stock");
        Assert.NotNull(stock);

        var large = stock.Single(r => r.EggGradeId == grades["Large"]);
        Assert.Equal(1000, large.Available);
        Assert.Equal(50, large.Restricted);
        Assert.Equal("Large", large.GradeName);

        var medium = stock.Single(r => r.EggGradeId == grades["Medium"]);
        Assert.Equal(300, medium.Available);
        Assert.Equal(0, medium.Restricted);
    }

    [Fact]
    public async Task Stock_IsTenantScoped()
    {
        var (_, accountA, _, gradesA) = await SetupAsync("Large");
        await factory.SeedEggLotAsync(accountA, gradesA["Large"], 500);

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var stock = await clientB.GetFromJsonAsync<List<StockDto>>("/api/v1/stock");
        Assert.Empty(stock!);
    }

    [Fact]
    public async Task GetSalesOrder_ReturnsItems_ListFiltersByStatus()
    {
        var (client, accountId, _, grades) = await SetupAsync("Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 500);
        var draftId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);
        var confirmedId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 200);
        await client.PostWithKeyAsync($"/api/v1/sales/{confirmedId}/confirm", Guid.NewGuid().ToString());

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{confirmedId}");
        Assert.Equal("Confirmed", order!.Status);
        var item = Assert.Single(order.Items);
        Assert.Equal(grades["Large"], item.EggGradeId);
        Assert.Equal(200, item.Quantity);

        var drafts = await client.GetFromJsonAsync<List<OrderDto>>("/api/v1/sales?status=draft");
        Assert.Contains(drafts!, o => o.Id == draftId);
        Assert.DoesNotContain(drafts!, o => o.Id == confirmedId);

        var bad = await client.GetAsync("/api/v1/sales?status=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // #769 — one confirmed order for `quantity` x the product's list price on
    // `date`, settled by `paidMinorUnits`. Built through the API on purpose:
    // the seeder's orders price every line at zero, which would make every
    // order settled the moment it was confirmed and hide the whole feature.
    private static async Task<Guid> ConfirmedOrderAsync(
        HttpClient client, Guid customerId, Guid productId,
        DateOnly date, int quantity, long paidMinorUnits)
    {
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = date });
        var orderId = (await order.Content.ReadFromJsonAsync<IdDto>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity });
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        if (paidMinorUnits > 0)
        {
            var pay = await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/payments",
                Guid.NewGuid().ToString(),
                new { paymentDate = date, amountMinorUnits = paidMinorUnits, method = "Cash" });
            Assert.Equal(HttpStatusCode.Created, pay.StatusCode);
        }
        return orderId;
    }

    private async Task<(HttpClient Client, Guid CustomerId, Guid ProductId)> SalesSetupAsync(
        Guid accountId, Guid farmId, Guid gradeId, HttpClient client)
    {
        var productId = await factory.SeedProductAsync(
            accountId, farmId, gradeId, $"P-{Guid.NewGuid():N}"[..12], 100);
        await factory.SeedEggLotAsync(accountId, gradeId, 500);
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..14], phone = "555-0000" });
        var customerId = (await customer.Content.ReadFromJsonAsync<IdDto>())!.Id;
        return (client, customerId, productId);
    }

    // The test the whole design turns on. The NEWEST order is the settled one,
    // so it occupies a slot on the first page: a filter applied in the browser
    // over `limit=2` would return one row, and only a server-side predicate
    // over the whole result set can return both owing orders.
    [Fact]
    public async Task SalesList_UnpaidFilter_IsEvaluatedServerSide_NotOverThePage()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large");
        var (_, customerId, productId) =
            await SalesSetupAsync(accountId, farmId, grades["Large"], client);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // 10 x 100 = 1000 minor units each.
        var settled = await ConfirmedOrderAsync(client, customerId, productId, today, 10, 1000);
        var owesAll = await ConfirmedOrderAsync(
            client, customerId, productId, today.AddDays(-1), 10, 0);
        var owesSome = await ConfirmedOrderAsync(
            client, customerId, productId, today.AddDays(-2), 10, 400);

        var page = (await client.GetFromJsonAsync<List<OrderMoneyDto>>(
            "/api/v1/sales?unpaid=true&limit=2"))!;

        Assert.Equal([owesAll, owesSome], page.Select(o => o.Id));
        Assert.Equal(1000, page[0].OutstandingMinorUnits);
        Assert.Equal(600, page[1].OutstandingMinorUnits);
        Assert.DoesNotContain(page, o => o.Id == settled);

        // The control: unfiltered, the settled order IS the newest row and
        // carries a zero rather than vanishing.
        var all = await client.GetFromJsonAsync<List<OrderMoneyDto>>("/api/v1/sales?limit=2");
        Assert.Equal(settled, all![0].Id);
        Assert.Equal(0, all[0].OutstandingMinorUnits);
    }

    [Fact]
    public async Task SalesList_VoidedPaymentDoesNotReduceOutstanding()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large");
        var (_, customerId, productId) =
            await SalesSetupAsync(accountId, farmId, grades["Large"], client);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var orderId = await ConfirmedOrderAsync(client, customerId, productId, today, 10, 1000);

        var settledRow = (await client.GetFromJsonAsync<List<OrderMoneyDto>>("/api/v1/sales"))!
            .Single(o => o.Id == orderId);
        Assert.Equal(0, settledRow.OutstandingMinorUnits);
        Assert.Empty((await client.GetFromJsonAsync<List<OrderMoneyDto>>(
            "/api/v1/sales?unpaid=true"))!);

        var payment = (await client.GetFromJsonAsync<PaymentsPageDto>(
            $"/api/v1/sales/{orderId}/payments"))!.Items.Single();
        Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
            $"/api/v1/payments/{payment.Id}/void", Guid.NewGuid().ToString(),
            new { version = payment.Version, reason = "wrong order" })).StatusCode);

        var afterVoid = (await client.GetFromJsonAsync<List<OrderMoneyDto>>("/api/v1/sales"))!
            .Single(o => o.Id == orderId);
        Assert.Equal(1000, afterVoid.OutstandingMinorUnits);
        // And the order comes back into the unpaid filter, which is the same
        // claim seen through the predicate rather than through the figure.
        Assert.Equal(orderId, Assert.Single(
            (await client.GetFromJsonAsync<List<OrderMoneyDto>>("/api/v1/sales?unpaid=true"))!).Id);
    }

    // Payments attach to confirmed orders only, so outstanding is undefined for
    // every other status — NULL, never a 0 that would read as settled.
    [Fact]
    public async Task SalesList_NonConfirmedOrders_CarryNoOutstanding_AndNeverMatchUnpaid()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large");
        var (_, customerId, productId) =
            await SalesSetupAsync(accountId, farmId, grades["Large"], client);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var draft = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = today });
        var draftId = (await draft.Content.ReadFromJsonAsync<IdDto>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{draftId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10 });

        var row = (await client.GetFromJsonAsync<List<OrderMoneyDto>>("/api/v1/sales"))!
            .Single(o => o.Id == draftId);
        Assert.Equal("Draft", row.Status);
        Assert.Equal(1000, row.TotalMinorUnits);
        Assert.Null(row.OutstandingMinorUnits);

        Assert.Empty((await client.GetFromJsonAsync<List<OrderMoneyDto>>(
            "/api/v1/sales?unpaid=true"))!);

        // Orthogonal controls, combinable: a status that can never be unpaid is
        // an empty 200, never a 400.
        var both = await client.GetAsync("/api/v1/sales?status=Draft&unpaid=true");
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
        Assert.Empty((await both.Content.ReadFromJsonAsync<List<OrderMoneyDto>>())!);
    }

    // The other two non-Confirmed statuses, each reached the only way it can
    // be. A Draft is the easy case and the one the test above happens to
    // build; on its own it leaves the repository's `Status == Confirmed`
    // interchangeable with `Status != Draft`, which would hand a Cancelled or
    // Voided order a live outstanding figure and put it back into a screen
    // that exists to show what customers still owe.
    [Fact]
    public async Task SalesList_CancelledAndVoidedOrders_CarryNoOutstanding_AndNeverMatchUnpaid()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large");
        var (_, customerId, productId) =
            await SalesSetupAsync(accountId, farmId, grades["Large"], client);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Cancelled: only a draft can be cancelled, so it is built and then
        // cancelled rather than confirmed first.
        var draft = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = today });
        var cancelledId = (await draft.Content.ReadFromJsonAsync<IdDto>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{cancelledId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10 });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostWithKeyAsync(
            $"/api/v1/sales/{cancelledId}/cancel", Guid.NewGuid().ToString())).StatusCode);

        // Voided: only a confirmed order can be voided, and only with no live
        // payments on it, so this one is confirmed and left unpaid.
        var voidedId = await ConfirmedOrderAsync(
            client, customerId, productId, today.AddDays(-1), 10, 0);
        Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
            $"/api/v1/sales/{voidedId}/void", Guid.NewGuid().ToString(),
            new { reason = "sold to the wrong buyer" })).StatusCode);

        var rows = (await client.GetFromJsonAsync<List<OrderMoneyDto>>("/api/v1/sales"))!;
        var cancelled = rows.Single(o => o.Id == cancelledId);
        var voided = rows.Single(o => o.Id == voidedId);

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Voided", voided.Status);
        // The control that gives the nulls their meaning: both orders carry a
        // real total, so a null outstanding is a statement that the figure is
        // undefined off Confirmed and not an artefact of an empty order.
        Assert.Equal(1000, cancelled.TotalMinorUnits);
        Assert.Equal(1000, voided.TotalMinorUnits);
        Assert.Null(cancelled.OutstandingMinorUnits);
        Assert.Null(voided.OutstandingMinorUnits);

        // Same claim through the predicate. Nothing in this account is
        // confirmed and owing, so the unpaid page is empty.
        Assert.Empty((await client.GetFromJsonAsync<List<OrderMoneyDto>>(
            "/api/v1/sales?unpaid=true"))!);
    }

    // #512 — detail and list must answer identically. A figure on one and a
    // null on the other is a lie about the same order, not an omission.
    [Fact]
    public async Task SalesOrderDetail_CarriesTheSameOutstandingAsTheList()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large");
        var (_, customerId, productId) =
            await SalesSetupAsync(accountId, farmId, grades["Large"], client);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var orderId = await ConfirmedOrderAsync(client, customerId, productId, today, 10, 250);

        var listed = (await client.GetFromJsonAsync<List<OrderMoneyDto>>("/api/v1/sales"))!
            .Single(o => o.Id == orderId);
        var detail = await client.GetFromJsonAsync<OrderMoneyDto>($"/api/v1/sales/{orderId}");

        Assert.Equal(750, listed.OutstandingMinorUnits);
        Assert.Equal(listed.OutstandingMinorUnits, detail!.OutstandingMinorUnits);
    }

    [Fact]
    public async Task SalesList_UnpaidWithMalformedValue_Is400()
    {
        var (client, _, _, _) = await SetupAsync("Large");

        var bad = await client.GetAsync("/api/v1/sales?unpaid=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task GetForeignDailyEntry_Returns404()
    {
        var (clientA, accountA, farmA, gradesA) = await SetupAsync("Large");
        var flockA = await factory.SeedFlockAsync(accountA, farmA);
        var create = await clientA.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmA, flockA, DateOnly.FromDateTime(DateTime.UtcNow.Date),
                [new { eggGradeId = gradesA["Large"], quantity = 100 }]));
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var response = await clientB.GetAsync($"/api/v1/daily-entries/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
