namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #727 — the per-farm discount ceiling at confirm time. Sales and Worker are
// bound by it; Owner and Manager are not. The refusal is a 422 and the order
// stays Draft, so the seller can take it to a manager rather than retrying
// blindly.
[Collection(IntegrationCollection.Name)]
public sealed class SalesDiscountCeilingTests(CluckworkWebApplicationFactory factory)
{
    // A 10% ceiling against a list price of 100 minor units: 90 is exactly on
    // the boundary and allowed, 89 is one minor unit past it.
    private const long ListPrice = 100;
    private const int TenPercent = 1_000;
    // Far more than any order below needs, so a refusal is unambiguously the
    // ceiling and never a shortfall. Every order is 10 units of a product sold
    // per EGG, whose conversion factor is 1, so a confirm draws exactly 10.
    private const int SeededStock = 5_000;

    private sealed record IdDto(Guid Id);
    private sealed record OrderItemDto(long UnitPriceMinorUnits, long? ListUnitPriceMinorUnits);
    private sealed record OrderDto(Guid Id, string Status, int Version, List<OrderItemDto> Items);
    private sealed record ProblemDto(string? Title, string? Detail);

    private sealed record Fixture(Guid AccountId, Guid ProductId);

    private async Task<Fixture> SeedFarmAsync()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"o-{Guid.NewGuid():N}@test.local");
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var productId = await factory.SeedProductAsync(
            accountId, farmId, grades["Large"], "Large Eggs", ListPrice);
        await factory.SeedEggLotAsync(accountId, grades["Large"], SeededStock);
        return new Fixture(accountId, productId);
    }

    private async Task<HttpClient> SeedUserAsync(Guid accountId, string? role)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role);
        return factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
    }

    private Task SetCeilingAsync(Guid accountId, int? basisPoints) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var account = await db.Accounts.SingleAsync();
            var result = account.UpdateSettings(
                account.Name, account.TimeZoneId, account.Locale, account.DefaultCurrencyCode,
                account.UnitSystem, account.FirstDayOfWeek, account.DateFormatOverride,
                account.TimeFormatOverride, account.Brand, account.DefaultStepperUnit,
                account.WorkerSaleAllocationPolicy, basisPoints, financialRowsExist: false);
            Assert.True(result.IsSuccess);
            await db.SaveChangesAsync();
        });

    private async Task<Guid> DraftAsync(Fixture farm, HttpClient client, long unitPrice, int quantity = 10)
    {
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));
        Assert.Equal(HttpStatusCode.Created, (await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId = farm.ProductId, quantity, unitPriceMinorUnits = unitPrice })).StatusCode);

        // The whole fixture is meaningless if the snapshot did not land: with a
        // NULL list price every case below would pass for the wrong reason.
        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal(ListPrice, order!.Items[0].ListUnitPriceMinorUnits);
        return orderId;
    }

    // The only real-world producer of a PreDating row is EF materializing what
    // #720's backfill relabelled, and SalesOrderItem.Create throws on the value
    // precisely so the application can never write one. So reproduce the
    // backfill itself: the column holds the member NAME, and a backfilled row
    // carries no list price.
    private Task MakeLinePreDatingAsync(Guid accountId, Guid orderId) =>
        factory.WithTenantScopeAsync(accountId, db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "SalesOrderItems"
            SET "ListPriceBasis" = 'PreDating', "ListUnitPriceMinorUnits" = NULL
            WHERE "SalesOrderId" = {orderId}
            """));

    private static async Task<Guid> CreatedId(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid orderId) =>
        client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString(),
            // #721 — a below-list line cannot be confirmed without a reason, and
            // CheckCanConfirm runs BEFORE the ceiling check. Without this body
            // every case here would refuse at DiscountReasonRequired and prove
            // nothing about the ceiling.
            new { discountReasonCode = "ManagerApproved" });

    private Task<(string Status, int Available)> SnapshotAsync(Guid accountId, Guid orderId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var order = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            var available = await db.EggLots.AsNoTracking().SumAsync(l => l.QuantityAvailable);
            return (order.Status.ToString(), available);
        });

    // --- the refusal --------------------------------------------------------

    [Fact]
    public async Task ASalesUserConfirmingAnOverCeilingOrder_Is422_AndTheOrderStaysDraft()
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, TenPercent);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice: 80); // 20% off

        var confirm = await ConfirmAsync(sales, orderId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
        var problem = (await confirm.Content.ReadFromJsonAsync<ProblemDto>())!;
        Assert.Equal("SalesOrder.DiscountCeilingExceeded", problem.Title);
        // The refusal names the offending line by grade, the measured percent
        // and the ceiling, so the seller knows what to ask for.
        Assert.Contains("Large", problem.Detail!);
        Assert.Contains("20.0%", problem.Detail!);
        Assert.Contains("10%", problem.Detail!);

        var (status, available) = await SnapshotAsync(farm.AccountId, orderId);
        Assert.Equal("Draft", status);
        Assert.Equal(SeededStock, available); // no stock was touched
    }

    // A plain Worker is bound by the ceiling too — only Owner and Manager are
    // not.
    [Fact]
    public async Task AWorkerConfirmingAnOverCeilingOrder_Is422()
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, TenPercent);
        var worker = await SeedUserAsync(farm.AccountId, null);
        var orderId = await DraftAsync(farm, worker, unitPrice: 80);

        var confirm = await ConfirmAsync(worker, orderId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
        Assert.Equal(
            "SalesOrder.DiscountCeilingExceeded",
            (await confirm.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
    }

    // The strict >: exactly at the ceiling is allowed, so a Sales user can
    // confirm a line discounted by exactly the farm's maximum.
    [Theory]
    [InlineData(90L, HttpStatusCode.OK)]                          // exactly 10% off
    [InlineData(89L, HttpStatusCode.UnprocessableEntity)]         // 11% off
    public async Task TheCeilingBoundaryIsInclusive(long unitPrice, HttpStatusCode expected)
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, TenPercent);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice);

        Assert.Equal(expected, (await ConfirmAsync(sales, orderId)).StatusCode);
    }

    // --- who is not bound ---------------------------------------------------

    [Theory]
    [InlineData(Roles.Owner)]
    [InlineData(Roles.Manager)]
    public async Task AnOwnerOrManagerConfirmsTheSameOrderUntouched(string role)
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, TenPercent);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice: 80);

        // Refused for the seller first, so the order under test is provably the
        // one the ceiling rejects.
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity, (await ConfirmAsync(sales, orderId)).StatusCode);

        var elevated = await SeedUserAsync(farm.AccountId, role);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(elevated, orderId)).StatusCode);

        var (status, available) = await SnapshotAsync(farm.AccountId, orderId);
        Assert.Equal("Confirmed", status);
        Assert.Equal(SeededStock - 10, available);
    }

    // --- a farm with no ceiling --------------------------------------------

    // The default, and the reason every existing farm is unaffected: NULL means
    // no ceiling, and nothing on the confirm path changes.
    [Fact]
    public async Task AFarmWithNoCeiling_ConfirmsADeepDiscountExactlyAsBefore()
    {
        var farm = await SeedFarmAsync();
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice: 1); // 99% off

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(sales, orderId)).StatusCode);

        var (status, available) = await SnapshotAsync(farm.AccountId, orderId);
        Assert.Equal("Confirmed", status);
        Assert.Equal(SeededStock - 10, available);
    }

    // ZERO is a legal setting and a DIFFERENT one from NULL: give nothing away.
    // A line at list still confirms; one minor unit below it does not.
    [Theory]
    [InlineData(100L, HttpStatusCode.OK)]
    [InlineData(99L, HttpStatusCode.UnprocessableEntity)]
    public async Task AZeroCeilingRefusesAnyDiscountAtAll(long unitPrice, HttpStatusCode expected)
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, 0);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice);

        // At list there is no discount, so #721 refuses a reason it was not
        // asked for — that case confirms with an empty body.
        var confirm = unitPrice == ListPrice
            ? await sales.PostWithKeyAsync(
                $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString())
            : await ConfirmAsync(sales, orderId);

        Assert.Equal(expected, confirm.StatusCode);
    }

    // --- the unmeasurable line ---------------------------------------------

    // A pre-#720 draft carries no list price, so it never trips
    // HasBelowListLine and #721 never asks for a reason. If the ceiling also
    // skipped it, a ceiling-bound user could give an unlimited discount with
    // nothing recorded anywhere — the exact hole this slice closes.
    [Fact]
    public async Task APreDatingLine_IsRefusedForASalesUser_WithNoFabricatedPercent()
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, TenPercent);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice: 80);
        await MakeLinePreDatingAsync(farm.AccountId, orderId);

        var confirm = await sales.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
        var problem = (await confirm.Content.ReadFromJsonAsync<ProblemDto>())!;
        Assert.Equal("SalesOrder.DiscountNotMeasurable", problem.Title);
        Assert.Contains("Large", problem.Detail!);
        // Never a percent: this line has no recorded list price, so any number
        // printed here would be fabricated.
        Assert.DoesNotContain("%", problem.Detail!);

        var (status, _) = await SnapshotAsync(farm.AccountId, orderId);
        Assert.Equal("Draft", status);
    }

    [Fact]
    public async Task APreDatingLine_IsConfirmableByAManager()
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, TenPercent);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice: 80);
        await MakeLinePreDatingAsync(farm.AccountId, orderId);

        var manager = await SeedUserAsync(farm.AccountId, Roles.Manager);
        var confirm = await manager.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var (status, available) = await SnapshotAsync(farm.AccountId, orderId);
        Assert.Equal("Confirmed", status);
        Assert.Equal(SeededStock - 10, available);
    }

    // --- the per-caller display hint on GET /account ------------------------

    private sealed record AccountDto(decimal? YourMaxDiscountPercent);

    // Null covers BOTH "the farm sets none" and "you may exceed it", because
    // both mean the same thing to the screen: show no ceiling warning.
    [Theory]
    [InlineData(Roles.Sales, true)]
    [InlineData(null, true)]            // a plain Worker is bound too
    [InlineData(Roles.Owner, false)]
    [InlineData(Roles.Manager, false)]
    public async Task GetAccount_CarriesTheCeilingOnlyForACallerBoundByIt(string? role, bool bound)
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, 1_250);
        var client = await SeedUserAsync(farm.AccountId, role);

        var account = await client.GetFromJsonAsync<AccountDto>("/api/v1/account");

        Assert.Equal(bound ? 12.5m : null, account!.YourMaxDiscountPercent);
    }

    [Fact]
    public async Task GetAccount_CarriesNoCeiling_WhenTheFarmSetsNone()
    {
        var farm = await SeedFarmAsync();
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);

        var account = await sales.GetFromJsonAsync<AccountDto>("/api/v1/account");

        Assert.Null(account!.YourMaxDiscountPercent);
    }

    // A zero ceiling is a real setting, not an absence — it must reach the
    // bound caller's screen as 0, never as "no ceiling".
    [Fact]
    public async Task GetAccount_CarriesAZeroCeiling_AsZeroAndNotAsNull()
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, 0);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);

        var account = await sales.GetFromJsonAsync<AccountDto>("/api/v1/account");

        Assert.Equal(0m, account!.YourMaxDiscountPercent);
    }

    // --- concurrency --------------------------------------------------------

    // The Version++ rule's parallel race, on the path this slice touches. Step
    // 5b reads the account row already locked FOR SHARE and mutates nothing, so
    // two managers racing the SAME over-ceiling order must still allocate
    // exactly once: one 200, one 409, one Sale movement, one Version bump.
    [Fact]
    public async Task TwoManagersConfirmingOneOverCeilingOrderConcurrently_AllocateItExactlyOnce()
    {
        var farm = await SeedFarmAsync();
        await SetCeilingAsync(farm.AccountId, TenPercent);
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice: 80);
        var versionBefore = (await sales.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}"))!.Version;

        var manager = await SeedUserAsync(farm.AccountId, Roles.Manager);
        var responses = await Task.WhenAll(
            ConfirmAsync(manager, orderId),
            ConfirmAsync(manager, orderId));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var (status, available) = await SnapshotAsync(farm.AccountId, orderId);
        Assert.Equal("Confirmed", status);
        Assert.Equal(SeededStock - 10, available); // drawn exactly once, never twice

        var (saleMovements, versionAfter) = await factory.WithTenantScopeAsync(
            farm.AccountId, async db => (
                await db.EggInventoryMovements.CountAsync(
                    m => m.MovementType == Domain.Eggs.EggMovementType.Sale),
                (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Version));
        Assert.Equal(1, saleMovements);
        Assert.Equal(versionBefore + 1, versionAfter);
    }

    // Design §4.9's falsifiable claim: the settings write stays on the PLAIN
    // OPTIMISTIC path because ConfirmSaleHandler holds the Accounts row FOR
    // SHARE from step 1 to commit, so a ceiling change either commits before
    // that lock is taken or waits behind it — no interleaving yields a torn
    // read. This is a copy of
    // SaleAllocationPolicyTests.ConfirmSale_ParksOnTheAccountLock_AndReadsAPolicyChangeThatCommittedWhileItWaited
    // with the ceiling in place of the policy.
    //
    // If this goes RED on the plain path, §4.9 was wrong and the settings write
    // needs re-deciding — it must NOT be answered by quietly escalating the
    // settings handler to FOR UPDATE.
    [Fact]
    public async Task ConfirmSale_ParksOnTheAccountLock_AndReadsACeilingThatCommittedWhileItWaited()
    {
        var farm = await SeedFarmAsync();
        // The farm starts with NO ceiling, so a stale read would confirm.
        var sales = await SeedUserAsync(farm.AccountId, Roles.Sales);
        var orderId = await DraftAsync(farm, sales, unitPrice: 80); // 20% off

        // The fence: an exclusive lock on the Account row, standing in for an
        // in-flight settings write that has not committed yet. Built directly,
        // not via factory.Services (#269), for hand-held control of the
        // transaction across several steps.
        var tenant = new TenantContext();
        tenant.Resolve(farm.AccountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
        await using var fence = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {farm.AccountId} FOR UPDATE""");
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "Accounts" SET "MaxDiscountBasisPoints" = {TenPercent}, "Version" = "Version" + 1
            WHERE "Id" = {farm.AccountId}
            """);
        var holderPid = await fenceDb.BackendPidAsync();

        var confirm = ConfirmAsync(sales, orderId);

        var blocked = await factory.WaitUntilDoneOrBlockedAsync(confirm, holderPid);
        Assert.True(blocked, "ConfirmSaleHandler must park on the account row's shared lock, not read the stale ceiling");

        await fence.CommitAsync();
        var response = await confirm;

        // Reads the COMMITTED ceiling, not the null it would have seen before
        // parking. A stale read gives 200 and decrements stock.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "SalesOrder.DiscountCeilingExceeded",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);

        var (status, available) = await SnapshotAsync(farm.AccountId, orderId);
        Assert.Equal("Draft", status);
        Assert.Equal(SeededStock, available);
    }
}
