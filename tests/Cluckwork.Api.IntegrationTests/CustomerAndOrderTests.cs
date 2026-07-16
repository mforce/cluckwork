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
    private sealed record OrderItemDto(Guid Id, Guid EggGradeId, int Quantity, long UnitPriceMinorUnits);
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
    public async Task ParallelAddItems_TotalMatchesPersistedItems()
    {
        // The race both reviews flagged: without AddItem's Version bump, two
        // parallel add-items both commit but the second overwrites the first's
        // TotalAmount. With the bump, the loser 409s and rolls back entirely —
        // either way the denormalized total must equal the sum of persisted lines.
        var (client, _, _, grades) = await SetupAsync("Large");
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var a = client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        var b = client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 20, unitPriceMinorUnits = 100 });
        var responses = await Task.WhenAll(a, b);

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"unexpected {(int)r.StatusCode}"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        var expected = order!.Items.Sum(i => i.Quantity * i.UnitPriceMinorUnits);
        Assert.Equal(expected, order.TotalMinorUnits);
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.Created), order.Items.Count);
    }

    [Fact]
    public async Task Customer_WhitespaceNamePhone_400NotServerError()
    {
        var (client, _, _, _) = await SetupAsync("Large");
        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "   ", phone = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_OverflowingLineTotal_Rejected()
    {
        var (client, _, _, grades) = await SetupAsync("Large");
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var response = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 2, unitPriceMinorUnits = long.MaxValue });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CancelDraft_Succeeds_CancelConfirmed_409()
    {
        var (client, accountId, _, grades) = await SetupAsync("Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 500);
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));

        // Cancel a draft: 204, listed as Cancelled, and rejects further mutation.
        var draftId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));
        var cancel = await client.PostWithKeyAsync(
            $"/api/v1/sales/{draftId}/cancel", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        var cancelled = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{draftId}");
        Assert.Equal("Cancelled", cancelled!.Status);

        var addToCancelled = await client.PostWithKeyAsync(
            $"/api/v1/sales/{draftId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 1, unitPriceMinorUnits = 1 });
        Assert.Equal(HttpStatusCode.Conflict, addToCancelled.StatusCode);
        var confirmCancelled = await client.PostWithKeyAsync(
            $"/api/v1/sales/{draftId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, confirmCancelled.StatusCode);

        // A confirmed order cannot be cancelled.
        var confirmedId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));
        await client.PostWithKeyAsync(
            $"/api/v1/sales/{confirmedId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        await client.PostWithKeyAsync(
            $"/api/v1/sales/{confirmedId}/confirm", Guid.NewGuid().ToString());
        var cancelConfirmed = await client.PostWithKeyAsync(
            $"/api/v1/sales/{confirmedId}/cancel", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, cancelConfirmed.StatusCode);
    }

    [Fact]
    public async Task EditAndRemoveLines_TotalTracks()
    {
        var (client, accountId, _, grades) = await SetupAsync("Large", "Medium");
        // Stock so the confirm at the end actually succeeds (else the order stays
        // Draft and the final 409 assertion would be testing nothing).
        await factory.SeedEggLotAsync(accountId, grades["Medium"], 100);
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        // two lines: 10x100 + 5x200 = 2000
        var addA = await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        var itemA = (await addA.Content.ReadFromJsonAsync<ItemCreatedDto>())!.ItemId;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = grades["Medium"], quantity = 5, unitPriceMinorUnits = 200 });

        // edit line A -> 4x250 = 1000; total 2000
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{orderId}/items/{itemA}")
        {
            Content = JsonContent.Create(new { quantity = 4, unitPriceMinorUnits = 250 }),
        };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var update = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var afterUpdate = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal(2000, afterUpdate!.TotalMinorUnits);

        // remove line A -> total 1000
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sales/{orderId}/items/{itemA}");
        del.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var remove = await client.SendAsync(del);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        var afterRemove = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal(1000, afterRemove!.TotalMinorUnits);
        Assert.Single(afterRemove.Items);

        // mutating a confirmed order's lines -> 409
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        var delConfirmed = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/sales/{orderId}/items/{afterRemove.Items[0].Id}");
        delConfirmed.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var removeConfirmed = await client.SendAsync(delConfirmed);
        Assert.Equal(HttpStatusCode.Conflict, removeConfirmed.StatusCode);
    }

    private sealed record ItemCreatedDto(Guid OrderId, Guid ItemId);

    [Fact]
    public async Task FullLoop_PureHttp_RecordToSale()
    {
        // The complete MVP loop with no harness data seeding beyond
        // account/user/grades: record production -> submit -> lots -> customer ->
        // order -> items -> confirm -> stock decremented. This is what a real
        // client (the SPA) will do.
        var (client, accountId, farmId, grades) = await SetupAsync("Large", "Medium");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
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
        Assert.NotNull(stock);
        Assert.Equal(350, stock.Single(s => s.EggGradeId == grades["Large"]).Available);
        Assert.Equal(200, stock.Single(s => s.EggGradeId == grades["Medium"]).Available);
    }
}
