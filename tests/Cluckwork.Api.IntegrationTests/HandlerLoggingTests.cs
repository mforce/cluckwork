namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Serilog.Events;

// #216 — money-path handlers narrate state transitions: Information on
// success with stable ids, Warning with the failure reason on a failed
// Result. AccountId rides on every in-request event via the tenant
// middleware's logging scope (spec §10), so a single grep tells the story
// of one account's day. Shares the request-logging collection: same host,
// same sink tap; asserts filter by handler SourceContext, so the classes
// can't contaminate each other.
[Collection(RequestLoggingCollection.Name)]
public sealed class HandlerLoggingTests(RequestLoggingFactory factory)
{
    private IReadOnlyList<LogEvent> EventsFrom(string handler) =>
        [.. factory.Sink.Events.Where(e => ScalarOf(e, "SourceContext")?.EndsWith(handler) == true)];

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Guid FlockId, Dictionary<string, Guid> Grades)>
        SetupAsync(params string[] gradeNames)
    {
        var email = $"hlog-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, gradeNames);
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, flockId, grades);
    }

    [Fact]
    public async Task Submitting_a_daily_entry_logs_information_with_stable_ids_and_account_scope()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("HL-Large");
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId,
            houseId = Guid.NewGuid(),
            flockId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            totalEggs = 100,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["HL-Large"], quantity = 100 } }
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, record.StatusCode);
        var entryId = (await record.Content.ReadFromJsonAsync<CreatedDto>())!.Id;

        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        Assert.Equal(System.Net.HttpStatusCode.OK, submit.StatusCode);
        var logged = Assert.Single(EventsFrom("SubmitDailyEntryHandler"),
            e => e.Level == LogEventLevel.Information
                 && ScalarOf(e, "DailyEntryId") == entryId.ToString());
        Assert.Equal(flockId.ToString(), ScalarOf(logged, "FlockId"));
        Assert.Equal(accountId.ToString(), ScalarOf(logged, "AccountId"));
    }

    [Fact]
    public async Task Confirming_a_sale_logs_information_with_the_order_id()
    {
        var (client, accountId, farmId, _, grades) = await SetupAsync("HL-Conf");
        await factory.SeedEggLotAsync(accountId, grades["HL-Conf"], quantity: 500);
        var orderId = await factory.SeedSalesOrderAsync(accountId, grades["HL-Conf"], 200);

        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(System.Net.HttpStatusCode.OK, confirm.StatusCode);
        var logged = Assert.Single(EventsFrom("ConfirmSaleHandler"),
            e => e.Level == LogEventLevel.Information
                 && ScalarOf(e, "SalesOrderId") == orderId.ToString());
        Assert.Equal(accountId.ToString(), ScalarOf(logged, "AccountId"));
    }

    [Fact]
    public async Task Failed_allocation_logs_a_warning_with_the_failure_reason()
    {
        // GUID-suffixed grade name: the warning is correlated through the
        // ErrorDescription text, so the marker must be unique per run.
        var marker = $"HL-Short-{Guid.NewGuid():N}"[..16];
        var (client, accountId, _, _, grades) = await SetupAsync(marker);
        await factory.SeedEggLotAsync(accountId, grades[marker], quantity: 50);
        var orderId = await factory.SeedSalesOrderAsync(accountId, grades[marker], 300);

        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.False(confirm.IsSuccessStatusCode);
        // The unique grade name in the description ties the warning to THIS
        // test's order — the sink is shared across the class.
        var logged = Assert.Single(EventsFrom("ConfirmSaleHandler"),
            e => e.Level == LogEventLevel.Warning
                 && ScalarOf(e, "ErrorDescription")?.Contains(marker) == true);
        Assert.Equal("EggLot.InsufficientStock", ScalarOf(logged, "ErrorCode"));
        Assert.Equal(accountId.ToString(), ScalarOf(logged, "AccountId"));
    }

    [Fact]
    public async Task Recording_a_payment_logs_information_with_order_and_payment_ids()
    {
        var (client, accountId, farmId, _, grades) = await SetupAsync("HL-Pay");
        await factory.SeedEggLotAsync(accountId, grades["HL-Pay"], quantity: 500);
        // Seeded orders carry zero-price lines (total 0 -> any payment is an
        // overpay), so build this order through the API with a real price.
        var productId = await factory.SeedProductAsync(accountId, farmId, grades["HL-Pay"]);
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"HL Buyer {Guid.NewGuid():N}"[..20], phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) });
        var orderId = (await order.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 50, unitPriceMinorUnits = 100 });
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(System.Net.HttpStatusCode.OK, confirm.StatusCode);

        var pay = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/payments", Guid.NewGuid().ToString(), new
            {
                paymentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                amountMinorUnits = 100L,
                method = "Cash",
                referenceNumber = (string?)null
            });

        Assert.Equal(System.Net.HttpStatusCode.Created, pay.StatusCode);
        var logged = Assert.Single(EventsFrom("RecordPaymentHandler"),
            e => e.Level == LogEventLevel.Information
                 && ScalarOf(e, "SalesOrderId") == orderId.ToString());
        Assert.NotNull(ScalarOf(logged, "PaymentId"));
        Assert.Equal(accountId.ToString(), ScalarOf(logged, "AccountId"));
    }

    private sealed record CreatedDto(Guid Id);
}
