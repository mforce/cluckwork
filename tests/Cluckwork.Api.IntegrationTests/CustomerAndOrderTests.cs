namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #10 + #11 — customers and the order create/add-item surface. The final test
// drives the whole MVP loop through the public API alone (no harness seeding
// beyond the account/user/grades).
[Collection(IntegrationCollection.Name)]
public sealed class CustomerAndOrderTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record CustomerDto(
        Guid Id, string Name, string Phone, string? Email, string? Address, string? Note, int Version);
    private sealed record OrderItemDto(Guid Id, Guid EggGradeId, int Quantity, long UnitPriceMinorUnits);
    private sealed record OrderDto(
        Guid Id, Guid CustomerId, string ReferenceNumber, string Status,
        long TotalMinorUnits, string CurrencyCode, List<OrderItemDto> Items);
    private sealed record StockDto(Guid EggGradeId, int Available, int Restricted);

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId,
        Dictionary<string, Guid> Grades, Dictionary<string, Guid> Products)>
        SetupAsync(params string[] gradeNames)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, gradeNames);
        // One product per grade (#99): sales lines sell products, unit Egg →
        // factor 1, so the old per-egg quantities/prices read the same.
        var products = new Dictionary<string, Guid>();
        foreach (var (name, gradeId) in grades)
            products[name] = await factory.SeedProductAsync(
                accountId, farmId, gradeId, $"{name} Eggs");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, grades, products);
    }

    private static async Task<Guid> CreatedId(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    [Fact]
    public async Task Customer_CreateGetList_RoundTrips()
    {
        var (client, _, _, _, products) = await SetupAsync("Large");

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
        var (client, _, _, _, products) = await SetupAsync("Large");
        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "No Phone" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_UnknownCustomer_404()
    {
        var (client, _, _, _, products) = await SetupAsync("Large");
        var response = await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId = Guid.NewGuid(), orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_ForeignCustomer_404()
    {
        var (clientA, _, _, _, _) = await SetupAsync("Large");
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
    public async Task AddItem_UnknownProduct_422_And_NonDraft_409()
    {
        var (client, accountId, _, grades, products) = await SetupAsync("Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 500);

        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var unknownProduct = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = Guid.NewGuid(), quantity = 10, unitPriceMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownProduct.StatusCode);

        await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var addAfterConfirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 5, unitPriceMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.Conflict, addAfterConfirm.StatusCode);
    }

    [Fact]
    public async Task ParallelAddItems_TotalMatchesPersistedItems()
    {
        // The race both reviews flagged: without AddItem's Version bump, two
        // parallel add-items both commit but the second overwrites the first's
        // TotalAmount. With the bump, the loser 409s and rolls back entirely —
        // either way the denormalized total must equal the sum of persisted lines.
        var (client, _, _, grades, products) = await SetupAsync("Large");
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var a = client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        var b = client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 20, unitPriceMinorUnits = 100 });
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
        var (client, _, _, _, products) = await SetupAsync("Large");
        var response = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "   ", phone = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_OverflowingLineTotal_Rejected()
    {
        var (client, _, _, grades, products) = await SetupAsync("Large");
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var response = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 2, unitPriceMinorUnits = long.MaxValue });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // #398 — a fractional quantity (e.g. 2.5) used to fail deep inside
    // minimal-API JSON binding, before AddOrderItemValidator or the handler
    // ever ran, and Program.cs's /error handler echoed the raw
    // BadHttpRequestException.Message straight back: `Failed to read
    // parameter "AddOrderItemRequest request" from the request body as
    // JSON.` This pins the safe replacement — the same ValidationProblem
    // `errors` shape every other 400 in this app uses, with none of the
    // parameter/type internals in the body.
    [Fact]
    public async Task AddItem_FractionalQuantity_StableProblemDetails_NotBindingExceptionText()
    {
        var (client, _, _, _, products) = await SetupAsync("Large");
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var response = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 2.5m, unitPriceMinorUnits = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        // The old leak: the raw parameter-binding exception text must be gone.
        Assert.DoesNotContain("Failed to read parameter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AddOrderItemRequest", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Utf8JsonReader", body, StringComparison.Ordinal);
        // Same shape as every other 400 in this app: a ValidationProblem errors dict.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.GetProperty("errors").ValueKind);
    }

    // The control: without this, the fractional-quantity test above proves
    // nothing — a change that broke ordinary integer-quantity/decimal-priced
    // adds entirely would look identical from that test's point of view alone.
    [Fact]
    public async Task AddItem_IntegerQuantityAndDecimalUnitPrice_StillWorks()
    {
        var (client, _, _, _, products) = await SetupAsync("Large");
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        // $2.50/unit (decimal money, carried on the wire as minor units per
        // the app's existing decimal-to-minor-units convention) × 7 whole units.
        var response = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 7, unitPriceMinorUnits = 250 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        var item = Assert.Single(order!.Items);
        Assert.Equal(7, item.Quantity);
        Assert.Equal(250, item.UnitPriceMinorUnits);
        Assert.Equal(1750, order.TotalMinorUnits);
    }

    // Same JSON-binding defect class, the OTHER request DTO
    // (UpdateOrderItemRequest — the edit-line endpoint): proves the /error
    // fix isn't accidentally tied to one specific parameter type name.
    [Fact]
    public async Task UpdateItem_FractionalQuantity_StableProblemDetails_NotBindingExceptionText()
    {
        var (client, _, _, _, products) = await SetupAsync("Large");
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));
        var added = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Large"], quantity = 3, unitPriceMinorUnits = 100 });
        var itemId = (await added.Content.ReadFromJsonAsync<ItemCreatedDto>())!.ItemId;

        var response = await client.PutWithKeyAsync(
            $"/api/v1/sales/{orderId}/items/{itemId}", Guid.NewGuid().ToString(),
            new { quantity = 1.5m, unitPriceMinorUnits = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Content-Type + a parseable ValidationProblem body — not just "no
        // leak", which an EMPTY body would also trivially satisfy. Outside
        // Development, ASP.NET Core's own binding failure sets StatusCode
        // directly with NO body at all unless RouteHandlerOptions.
        // ThrowOnBadRequest is forced true (Program.cs, #398) so this
        // reaches /error in the first place — a weaker check here would stay
        // green even with that registration missing.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Failed to read parameter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateOrderItemRequest", body, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.GetProperty("errors").ValueKind);
    }

    [Fact]
    public async Task CancelDraft_Succeeds_CancelConfirmed_409()
    {
        var (client, accountId, _, grades, products) = await SetupAsync("Large");
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
            new { productId = products["Large"], quantity = 1, unitPriceMinorUnits = 1 });
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
            new { productId = products["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        await client.PostWithKeyAsync(
            $"/api/v1/sales/{confirmedId}/confirm", Guid.NewGuid().ToString());
        var cancelConfirmed = await client.PostWithKeyAsync(
            $"/api/v1/sales/{confirmedId}/cancel", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, cancelConfirmed.StatusCode);
    }

    [Fact]
    public async Task EditAndRemoveLines_TotalTracks()
    {
        var (client, accountId, _, grades, products) = await SetupAsync("Large", "Medium");
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
            new { productId = products["Large"], quantity = 10, unitPriceMinorUnits = 100 });
        var itemA = (await addA.Content.ReadFromJsonAsync<ItemCreatedDto>())!.ItemId;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Medium"], quantity = 5, unitPriceMinorUnits = 200 });

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
        var (client, accountId, farmId, grades, products) = await SetupAsync("Large", "Medium");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // 1. record + submit production
        // #394: submit requires exact reconciliation — 918 total − 10 cracked −
        // 5 dirty − 3 discarded = 900 sellable, matching the two grade lines.
        var entryId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId, houseId = Guid.NewGuid(), flockId, date = today,
                totalEggs = 918, crackedEggs = 10, dirtyEggs = 5, discardedEggs = 3, mortalityCount = 0,
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
            new { productId = products["Large"], quantity = 250, unitPriceMinorUnits = 30 });
        await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = products["Medium"], quantity = 100, unitPriceMinorUnits = 25 });

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

    // #494 — creation wasn't on the audit trail at all; only corrections were.
    [Fact]
    public async Task SalesOrder_Create_WritesAuditEvent()
    {
        var (client, accountId, _, _, _) = await SetupAsync("Large");

        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Mercado Central", phone = "555-0100" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "SalesOrder" && e.EntityId == orderId)
            .ToListAsync());

        var created = Assert.Single(events);
        Assert.Equal("SalesOrder.Create", created.Action);
    }

    [Fact]
    public async Task Customer_Update_Success_AllFieldsAndVersionBump()
    {
        var (client, _, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000" }));

        var update = await client.PutWithKeyAsync(
            $"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
            new { version = 0, name = "  Updated Name  ", phone = "  555-9999  ", email = "u@example.com", address = "New Addr", note = "New Note" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var got = await client.GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{id}");
        Assert.Equal("Updated Name", got!.Name);
        Assert.Equal("555-9999", got.Phone);
        Assert.Equal("u@example.com", got.Email);
        Assert.Equal("New Addr", got.Address);
        Assert.Equal("New Note", got.Note);
        Assert.Equal(1, got.Version);
    }

    [Fact]
    public async Task Customer_Update_InvalidBody_400()
    {
        var (client, _, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000" }));

        var response = await client.PutWithKeyAsync(
            $"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
            new { version = 0, name = "   ", phone = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Customer_Update_UnknownId_404()
    {
        var (client, _, _, _, _) = await SetupAsync("Large");
        var response = await client.PutWithKeyAsync(
            $"/api/v1/customers/{Guid.NewGuid()}", Guid.NewGuid().ToString(),
            new { version = 0, name = "Nope", phone = "1" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Customer_Update_ForeignCustomer_404_AndBRecordFullyUnchanged()
    {
        var (clientA, _, _, _, _) = await SetupAsync("Large");
        var emailB = $"cb-{Guid.NewGuid():N}@test.local";
        var accountB = await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));
        var customerBId = await CreatedId(await clientB.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "B's customer", phone = "555-1234", email = "b@example.com", address = "B Addr", note = "B Note" }));

        var response = await clientA.PutWithKeyAsync(
            $"/api/v1/customers/{customerBId}", Guid.NewGuid().ToString(),
            new { version = 0, name = "Hijacked", phone = "999" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var bRow = await factory.WithTenantScopeAsync(accountB, db =>
            db.Customers.SingleAsync(c => c.Id == customerBId));
        Assert.Equal("B's customer", bRow.Name);
        Assert.Equal("555-1234", bRow.Phone);
        Assert.Equal("b@example.com", bRow.Email);
        Assert.Equal("B Addr", bRow.Address);
        Assert.Equal("B Note", bRow.Note);
        Assert.Equal(0, bRow.Version);
    }

    [Fact]
    public async Task Customer_Update_StaleBaseVersion_ExactlyOneWinnerOverHttp()
    {
        var (client, accountId, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000" }));
        var loaded = await client.GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{id}");
        Assert.Equal(0, loaded!.Version);

        var responses = await Task.WhenAll(
            client.PutWithKeyAsync($"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
                new { version = loaded.Version, name = "Winner A", phone = "111" }),
            client.PutWithKeyAsync($"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
                new { version = loaded.Version, name = "Winner B", phone = "222" }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);

        var final = await factory.WithTenantScopeAsync(accountId, db => db.Customers.SingleAsync(c => c.Id == id));
        Assert.Equal(1, final.Version);
        Assert.True(final.Name is "Winner A" or "Winner B");
        Assert.True(
            (final.Name == "Winner A" && final.Phone == "111") ||
            (final.Name == "Winner B" && final.Phone == "222"),
            "final row must be wholly one winner's payload");
    }

    [Fact]
    public async Task Customer_Update_HeldSnapshots_SecondSaveThrowsDbUpdateConcurrencyException()
    {
        // Two open EF contexts holding the same row, saved A-then-B — the direct
        // IsConcurrencyToken() proof (FarmLogoTests precedent): a serialized HTTP
        // race can't show this since the host may just queue the two requests.
        //
        // Both contexts write the IDENTICAL name/phone (the same-value-update
        // precedent from CustomerTests: Update always bumps Version even when
        // nothing else changes). This isolates the claim to the Version TOKEN
        // alone — with differing field values, EF's own "which columns changed"
        // check could independently flag the second save even with the
        // concurrency token removed, so that shape would prove nothing about
        // IsConcurrencyToken() specifically. Same values leaves Version as the
        // ONLY thing that can possibly make B's save fail.
        var (client, accountId, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000" }));

        var conflict = await Record.ExceptionAsync(() =>
            factory.WithTenantScopeAsync(accountId, dbA =>
                factory.WithTenantScopeAsync(accountId, async dbB =>
                {
                    var a = await dbA.Customers.SingleAsync(c => c.Id == id);
                    var b = await dbB.Customers.SingleAsync(c => c.Id == id);

                    a.Update("Original", "555-0000", null, null, null);
                    await dbA.SaveChangesAsync();

                    b.Update("Original", "555-0000", null, null, null);
                    await dbB.SaveChangesAsync();
                })));

        var concurrencyException = Assert.IsType<DbUpdateConcurrencyException>(conflict);
        // The EF metadata itself names Version as the modified, conflicting
        // property on B's tracked entry — not merely "some exception fired".
        var entry = Assert.Single(concurrencyException.Entries);
        var versionProperty = entry.Property("Version");
        Assert.True(versionProperty.IsModified);
        Assert.Equal(0, versionProperty.OriginalValue);
        Assert.Equal(1, versionProperty.CurrentValue);

        var final = await factory.WithTenantScopeAsync(accountId, db => db.Customers.SingleAsync(c => c.Id == id));
        Assert.Equal("Original", final.Name); // both wrote the same value — the row is coherent either way
        Assert.Equal(1, final.Version); // A's save is the only one that committed
    }

    [Fact]
    public async Task Customer_CreateThenUpdate_WritesAuditEvents_WithResolvedActor()
    {
        var (client, accountId, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000" }));
        var update = await client.PutWithKeyAsync(
            $"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
            new { version = 0, name = "Updated", phone = "111" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "Customer" && e.EntityId == id)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync());

        Assert.Equal(2, events.Count);
        Assert.Equal("Customer.Create", events[0].Action);
        Assert.Equal("Customer.Update", events[1].Action);

        // SetupAsync seeds exactly one (Owner) user for this fresh account —
        // the one the client is authenticated as. Asserted POSITIVELY against
        // that real user's id AND email, never just "!= empty": the negative
        // form passes for any wrong-but-non-placeholder value (a stale actor,
        // a different account's user), which is most of the ways this can
        // regress (#500 precedent, DemoSeedAttributionTests).
        var actor = await factory.WithTenantScopeAsync(accountId, db => db.Users.SingleAsync(u => u.AccountId == accountId));
        Assert.All(events, e =>
        {
            Assert.Equal(actor.Id, e.ActorUserId);
            Assert.Equal(actor.Email, e.ActorEmail);
        });
    }

    [Fact]
    public async Task Customer_Update_RejectedVersionMismatch_AddsNoUpdateAuditRow()
    {
        var (client, accountId, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000" }));

        var response = await client.PutWithKeyAsync(
            $"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
            new { version = 5, name = "Nope", phone = "111" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The deterministic single-client stale-version path (the explicit
        // Version check in UpdateCustomerHandler, not the EF race path Program
        // maps separately) — real backend code/message, no new localization.
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Customer.VersionMismatch", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "This customer was changed since you loaded it — reload and retry.",
            doc.RootElement.GetProperty("detail").GetString());

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "Customer" && e.EntityId == id)
            .ToListAsync());
        Assert.DoesNotContain(events, e => e.Action == "Customer.Update");
    }

    // #625 review round 1 — bypassing FluentValidation via the domain method
    // must not be possible: the endpoint's validator is the boundary a
    // MaxLength violation is caught at, before the handler/domain ever runs.
    [Fact]
    public async Task Customer_Update_NameExceedsMaxLength_400WithExactErrorCode()
    {
        var (client, _, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000" }));

        var response = await client.PutWithKeyAsync(
            $"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
            new { version = 0, name = new string('a', Cluckwork.Domain.Sales.Customer.MaxNameLength + 1), phone = "555-0000" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("errorCodes", out var codes));
        Assert.Contains("Customer.Name.MaxLength", codes.GetRawText());
    }

    // #625 review round 1 — Update() normalizes blank optionals to null; this
    // proves it end to end: a customer created with every optional field
    // populated, then PUT with them blanked, persists as null (not "").
    [Fact]
    public async Task Customer_Update_BlankPopulatedOptionals_PersistsAsNull()
    {
        var (client, _, _, _, _) = await SetupAsync("Large");
        var id = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Original", phone = "555-0000", email = "orig@example.com", address = "Orig Addr", note = "Orig Note" }));

        var update = await client.PutWithKeyAsync(
            $"/api/v1/customers/{id}", Guid.NewGuid().ToString(),
            new { version = 0, name = "Original", phone = "555-0000", email = "", address = "   ", note = "" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var got = await client.GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{id}");
        Assert.Null(got!.Email);
        Assert.Null(got.Address);
        Assert.Null(got.Note);
    }
}
