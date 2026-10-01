namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #89 — customer payments: order-attached, currency copied from the order,
// no-overpay under the order row lock, void-not-delete, and the order-void
// guard ("void the payments first").
[Collection(IntegrationCollection.Name)]
public sealed class PaymentsTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record PaymentDto(
        Guid Id, Guid SalesOrderId, Guid CustomerId, DateOnly PaymentDate,
        long AmountMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
        string Method, string? ReferenceNumber, string? Note,
        bool Voided, string? VoidReason, int Version);
    private sealed record OrderPaymentsDto(
        List<PaymentDto> Items, long PaidMinorUnits, long OutstandingMinorUnits,
        long TotalMinorUnits, string CurrencyCode, int CurrencyMinorUnit);
    private sealed record BalanceRow(
        Guid CustomerId, long ConfirmedTotalMinorUnits, long PaidMinorUnits, long OutstandingMinorUnits);
    private sealed record BalancesDto(List<BalanceRow> Items, string CurrencyCode, int CurrencyMinorUnit);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Guid FlockId, Guid ProductId)>
        SetupWithStockAsync(int stock = 600)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        // #99: sales lines sell products (unit Egg → factor 1 keeps the math).
        var productId = await factory.SeedProductAsync(accountId, farmId, grades["Large"], "Large Eggs");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId,
            houseId = Guid.NewGuid(),
            flockId,
            date = Today,
            totalEggs = stock,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = stock } }
        });
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        return (client, accountId, farmId, flockId, productId);
    }

    // Confirmed order: qty × 100 minor units → total = qty * 100.
    private static async Task<(Guid OrderId, Guid CustomerId)> CreateConfirmedOrderAsync(
        HttpClient client, Guid productId, int qty)
    {
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..20], phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = qty, unitPriceMinorUnits = 100 });
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return (orderId, customerId);
    }

    private static Task<HttpResponseMessage> PayAsync(
        HttpClient client, Guid orderId, long amount, string method = "Cash",
        string? reference = null) =>
        client.PostWithKeyAsync($"/api/v1/sales/{orderId}/payments", Guid.NewGuid().ToString(), new
        {
            paymentDate = Today,
            amountMinorUnits = amount,
            method,
            referenceNumber = reference
        });

    private static Task<OrderPaymentsDto> GetPaymentsAsync(HttpClient client, Guid orderId) =>
        client.GetFromJsonAsync<OrderPaymentsDto>($"/api/v1/sales/{orderId}/payments")!;

    [Fact]
    public async Task Payments_PartialThenSettle_OverpayRefused_CurrencyFromOrder()
    {
        var (client, _, _, _, productId) = await SetupWithStockAsync();
        var (orderId, _) = await CreateConfirmedOrderAsync(client, productId, 50); // total 5000

        Assert.Equal(HttpStatusCode.Created, (await PayAsync(client, orderId, 2000, "Check", "chk 1")).StatusCode);

        var after1 = await GetPaymentsAsync(client, orderId);
        Assert.Equal(2000, after1.PaidMinorUnits);
        Assert.Equal(3000, after1.OutstandingMinorUnits);
        Assert.Equal("Check", after1.Items.Single().Method);
        // Currency snapshots from the ORDER, not the account of the day.
        Assert.Equal(after1.CurrencyCode, after1.Items.Single().CurrencyCode);

        // Overpay: outstanding is 3000; 3001 refused, order state untouched.
        var over = await PayAsync(client, orderId, 3001);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Contains("outstanding", await over.Content.ReadAsStringAsync());

        // Exact remainder settles.
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(client, orderId, 3000)).StatusCode);
        var settled = await GetPaymentsAsync(client, orderId);
        Assert.Equal(0, settled.OutstandingMinorUnits);

        // Fully settled: any further payment is an overpay.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PayAsync(client, orderId, 1)).StatusCode);
    }

    [Fact]
    public async Task Payments_DraftOrder_Refused()
    {
        var (client, _, _, _, _) = await SetupWithStockAsync();
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..20], phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;

        var response = await PayAsync(client, orderId, 100);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("draft", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task VoidPayment_GrowsOutstandingBack_GuardsVersionAndDoubleVoid()
    {
        var (client, _, _, _, productId) = await SetupWithStockAsync();
        var (orderId, _) = await CreateConfirmedOrderAsync(client, productId, 10); // total 1000
        await PayAsync(client, orderId, 1000);
        var payment = (await GetPaymentsAsync(client, orderId)).Items.Single();

        // Stale base version → 409.
        var stale = await client.PostWithKeyAsync(
            $"/api/v1/payments/{payment.Id}/void", Guid.NewGuid().ToString(),
            new { version = payment.Version + 5, reason = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var ok = await client.PostWithKeyAsync(
            $"/api/v1/payments/{payment.Id}/void", Guid.NewGuid().ToString(),
            new { version = payment.Version, reason = "typed the wrong order" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var voided = await ok.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.True(voided!.Voided);
        Assert.Equal("typed the wrong order", voided.VoidReason);

        // The row stays, the money comes back off the settled total.
        var after = await GetPaymentsAsync(client, orderId);
        Assert.Single(after.Items);
        Assert.Equal(0, after.PaidMinorUnits);
        Assert.Equal(1000, after.OutstandingMinorUnits);

        // Voiding again: 409 (already voided).
        var again = await client.PostWithKeyAsync(
            $"/api/v1/payments/{payment.Id}/void", Guid.NewGuid().ToString(),
            new { version = voided.Version, reason = "again" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // The freed outstanding is payable again.
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(client, orderId, 1000)).StatusCode);
    }

    [Fact]
    public async Task VoidOrder_WithPayments_RefusedUntilPaymentsVoided()
    {
        var (client, _, _, _, productId) = await SetupWithStockAsync();
        var (orderId, _) = await CreateConfirmedOrderAsync(client, productId, 10);
        await PayAsync(client, orderId, 500);

        var refused = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/void", Guid.NewGuid().ToString(),
            new { reason = "wrong order" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("void the payments first", await refused.Content.ReadAsStringAsync());

        var payment = (await GetPaymentsAsync(client, orderId)).Items.Single();
        await client.PostWithKeyAsync(
            $"/api/v1/payments/{payment.Id}/void", Guid.NewGuid().ToString(),
            new { version = payment.Version, reason = "clearing for order void" });

        var allowed = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/void", Guid.NewGuid().ToString(),
            new { reason = "wrong order" });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // AGENTS.md race rule: two payments racing for the last of the outstanding
    // amount serialize on the order row — exactly one lands.
    [Fact]
    public async Task ParallelPayments_CannotOvershootTheTotal()
    {
        var (client, _, _, _, productId) = await SetupWithStockAsync();
        var (orderId, _) = await CreateConfirmedOrderAsync(client, productId, 10); // total 1000

        var responses = await Task.WhenAll(
            PayAsync(client, orderId, 700),
            PayAsync(client, orderId, 700));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));

        var after = await GetPaymentsAsync(client, orderId);
        Assert.Equal(700, after.PaidMinorUnits);
        Assert.Equal(300, after.OutstandingMinorUnits);
    }

    [Fact]
    public async Task ParallelVoids_SameBaseVersion_ExactlyOneWins()
    {
        var (client, _, _, _, productId) = await SetupWithStockAsync();
        var (orderId, _) = await CreateConfirmedOrderAsync(client, productId, 10);
        await PayAsync(client, orderId, 400);
        var payment = (await GetPaymentsAsync(client, orderId)).Items.Single();

        Task<HttpResponseMessage> Void(string reason) => client.PostWithKeyAsync(
            $"/api/v1/payments/{payment.Id}/void", Guid.NewGuid().ToString(),
            new { version = payment.Version, reason });

        var responses = await Task.WhenAll(Void("first"), Void("second"));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = (await GetPaymentsAsync(client, orderId)).Items.Single();
        Assert.True(after.Voided);
        Assert.Equal(payment.Version + 1, after.Version);
    }

    [Fact]
    public async Task CustomerBalances_SumConfirmedMinusSettled()
    {
        var (client, _, _, _, productId) = await SetupWithStockAsync();
        var (order1, customer1) = await CreateConfirmedOrderAsync(client, productId, 20); // 2000
        var (order2, customer2) = await CreateConfirmedOrderAsync(client, productId, 30); // 3000
        await PayAsync(client, order1, 500);
        await PayAsync(client, order2, 3000);

        var balances = await client.GetFromJsonAsync<BalancesDto>("/api/v1/customers/balances");
        var row1 = balances!.Items.Single(b => b.CustomerId == customer1);
        Assert.Equal(2000, row1.ConfirmedTotalMinorUnits);
        Assert.Equal(500, row1.PaidMinorUnits);
        Assert.Equal(1500, row1.OutstandingMinorUnits);

        var row2 = balances.Items.Single(b => b.CustomerId == customer2);
        Assert.Equal(0, row2.OutstandingMinorUnits);
    }
}
