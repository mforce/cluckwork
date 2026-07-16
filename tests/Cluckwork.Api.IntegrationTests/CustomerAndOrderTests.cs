namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #10 + #11 — customers and the order create/add-item surface. The final test
// drives the whole MVP loop through the public API alone (no harness seeding
// beyond the account/user/grades).
[Collection(IntegrationCollection.Name)]
public sealed class CustomerAndOrderTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record CustomerDto(
        Guid Id, string Name, string Phone, string? Email, string? Address, string? Note);
    private sealed record OrderItemDto(Guid EggGradeId, int Quantity, long UnitPriceMinorUnits);
    private sealed record OrderDto(
        Guid Id, Guid CustomerId, string ReferenceNumber, string Status,
        long TotalMinorUnits, string CurrencyCode, List<OrderItemDto> Items);
    private sealed record StockDto(Guid EggGradeId, int Available, int Restricted);

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

    private static async Task<Guid> CreatedId(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    [Fact]
    public async Task Customer_CreateGetList_RoundTrips()
    {
        var (client, _, _, _) = await SetupAsync("Large");

        var create = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "  Mercado Central ", phone = "555-0100", email = "buyer@mercado.test", note = "pays cash" });
        var id = await CreatedId(create);

        var got = await client.GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{id}");
        Assert.Equal("Mercado Central", got!.Name);      // trimmed
        Assert.Equal("555-0100", got.Phone);
        Assert.Equal("buyer@mercado.test", got.Email);
        Assert.Null(got.Address);
        Assert.Equal("pays cash", got.Note);

        var list = await client.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers");
        Assert.Contains(list!, c => c.Id == id);
    }

    [Fact]
    public async Task Customer_MissingPhone_Rejected()
    {
        var (client, _, _, _) = await SetupAsync("Large");
        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "No Phone" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_UnknownCustomer_404()
    {
        var (client, _, _, _) = await SetupAsync("Large");
        var response = await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId = Guid.NewGuid(), orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_ForeignCustomer_404()
    {
        var (clientA, _, _, _) = await SetupAsync("Large");
        var customerA = await CreatedId(await clientA.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "A's customer", phone = "1" }));

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var response = await clientB.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId = customerA, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_UnknownGrade_422_And_NonDraft_409()
    {
        var (client, accountId, _, grades) = await SetupAsync("Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 500);

        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var unknownGrade = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = Guid.NewGuid(), quantity = 10, unitPriceMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownGrade.StatusCode);

        await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var addAfterConfirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 5, unitPriceMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.Conflict, addAfterConfirm.StatusCode);
    }

    [Fact]
    public async Task FullLoop_PureHttp_RecordToSale()
    {
        // The complete MVP loop with no harness data seeding beyond
        // account/user/grades: record production -> submit -> lots -> customer ->
        // order -> items -> confirm -> stock decremented. This is what a real
        // client (the SPA) will do.
        var (client, _, farmId, grades) = await SetupAsync("Large", "Medium");
        var flockId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // 1. record + submit production
        var entryId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId, houseId = Guid.NewGuid(), flockId, date = today,
                totalEggs = 1000, crackedEggs = 10, dirtyEggs = 5, discardedEggs = 3, mortalityCount = 0,
                grades = new[]
                {
                    new { eggGradeId = grades["Large"], quantity = 600 },
                    new { eggGradeId = grades["Medium"], quantity = 300 }
                }
            }));
        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        // 2. customer + order + items
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Mercado Central", phone = "555-0100" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = today }));
        await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 250, unitPriceMinorUnits = 30 });
        await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Medium"], quantity = 100, unitPriceMinorUnits = 25 });

        // 3. confirm -> FIFO allocation
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // 4. order reflects items + snapshotted currency + totals
        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal("Confirmed", order!.Status);
        Assert.Equal(customerId, order.CustomerId);
        Assert.StartsWith("SO-", order.ReferenceNumber);
        Assert.Equal("USD", order.CurrencyCode);
        Assert.Equal(250 * 30 + 100 * 25, order.TotalMinorUnits);
        Assert.Equal(2, order.Items.Count);

        // 5. stock decremented per grade
        var stock = await client.GetFromJsonAsync<List<StockDto>>("/api/v1/stock");
        Assert.Equal(350, stock!.Single(s => s.EggGradeId == grades["Large"]).Available);
        Assert.Equal(200, stock.Single(s => s.EggGradeId == grades["Medium"]).Available);
    }
}
