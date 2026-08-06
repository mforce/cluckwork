namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Accounts.UpdateFarmSettings;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Expenses;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #162 — the ACTUAL close of §4.6's read-then-write window, the one
// CurrencyLockSerializationTests proves SERIALIZABLE cannot deliver: every
// handler that stamps Account.DefaultCurrencyCode onto a new row takes FOR
// SHARE on the account row inside its transaction, and the currency change
// takes FOR UPDATE. The two then genuinely serialize under READ COMMITTED,
// in both directions:
//
//   writer first  → the change blocks until the writer commits, then its
//                   probe SEES the row and refuses (Account.CurrencyLocked);
//   change first  → the writer blocks until the change commits, then reads
//                   and stamps the NEW currency.
//
// These tests hold one side's lock on a raw transaction and drive the REAL
// handler on the other side, so they pin the handlers' participation in the
// protocol — not a re-simulation of it. Blocking is detected via
// pg_blocking_pids against the holder's backend pid, never a timing guess.
[Collection(IntegrationCollection.Name)]
public sealed class CurrencyLockRaceTests(CluckworkWebApplicationFactory factory)
{
    private async Task<Guid> SeedFarmAsync()
    {
        var accountId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Race Farm", "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        return accountId;
    }

    private async Task<Guid> SeedCategoryAsync(Guid accountId)
    {
        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(ExpenseCategory.Create(
                categoryId, accountId, SeedDefaults.FarmId, $"Cat-{categoryId:N}"[..12]));
            await db.SaveChangesAsync();
        });
        return categoryId;
    }


    private static UpdateFarmSettingsCommand ChangeCurrencyCommand(Account account) => new(
        account.Name, account.TimeZoneId, account.Locale, "JPY",
        account.UnitSystem.ToString(), account.FirstDayOfWeek?.ToString(),
        account.DateFormatOverride, account.TimeFormatOverride,
        account.Brand, account.Version);

    [Fact]
    public async Task CurrencyChange_SerializesBehindAnInFlightMoneyWrite_AndRefuses()
    {
        var accountId = await SeedFarmAsync();
        var categoryId = await SeedCategoryAsync(accountId);
        Account snapshot = null!;
        await factory.WithTenantScopeAsync(accountId,
            async db => snapshot = await db.Accounts.AsNoTracking().SingleAsync());

        // A = a money write mid-flight: shared lock taken, first expense
        // inserted, transaction still open — exactly the shape every stamping
        // handler has after this slice. Built directly, not via
        // factory.Services (#269: that DbContext now carries
        // EnableRetryOnFailure, incompatible with this test's need for
        // precise, hand-held control of a transaction across several
        // separate steps raced against a real handler) — same pattern
        // ReportQueryBoundingTests/StepUpAuthTests use for exactly this
        // reason.
        var tenantA = new TenantContext();
        tenantA.Resolve(accountId);
        await using var dbA = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenantA);
        await using var transactionA = await dbA.Database.BeginTransactionAsync();
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR SHARE""");
        dbA.Expenses.Add(Expense.Create(
            Guid.NewGuid(), accountId, SeedDefaults.FarmId, categoryId,
            DateOnly.FromDateTime(DateTime.UtcNow.Date), "First ever expense", 12_00, "USD", 2));
        await dbA.SaveChangesAsync();
        var holderPid = await dbA.BackendPidAsync();

        // B = the real settings handler changing the currency.
        var change = Task.Run(async () =>
        {
            using var scopeB = factory.Services.CreateScope();
            scopeB.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            var handler = scopeB.ServiceProvider.GetRequiredService<UpdateFarmSettingsHandler>();
            return await handler.HandleAsync(ChangeCurrencyCommand(snapshot), CancellationToken.None);
        });

        var blocked = await factory.WaitUntilDoneOrBlockedAsync(change, holderPid);

        // Pre-#162 behavior: the change completed (successfully!) while the
        // writer was still uncommitted — the corruption this slice closes.
        Assert.True(blocked, "the currency change must park on the account row's lock, not race past it");

        await transactionA.CommitAsync();
        var result = await change;

        Assert.True(result.IsFailure, "the change must see the committed expense and refuse");
        Assert.Equal("Account.CurrencyLocked", result.Error.Code);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var account = await db.Accounts.AsNoTracking().SingleAsync();
            Assert.Equal("USD", account.DefaultCurrencyCode);
            var expense = await db.Expenses.AsNoTracking().SingleAsync();
            Assert.Equal("USD", expense.CurrencyCode);
        });
    }

    // The theory below proves each handler's locked READ blocks — but a
    // handler whose FOR SHARE evaporates on autocommit (read locked, insert
    // in a separate statement) would pass it too. This pins the transaction
    // boundary itself, writer-first, with REAL handlers on BOTH sides:
    //
    //   fence (raw FOR UPDATE) parks the writer at its read AND the settings
    //   change behind the same row; on release, Postgres wakes the queue in
    //   order — the writer's shared lock is granted first and the change's
    //   FOR UPDATE stays parked until the writer COMMITS. The change must
    //   then see the committed expense and refuse.
    //
    // Under the evaporating-lock mutant the change is no longer fenced by
    // the writer's transaction and (usually) slips between its read and its
    // insert — succeeding where this test demands refusal. That detection is
    // probabilistic (the writer can still win the sprint), but the protocol
    // assertion under CORRECT code is deterministic: queue order is FIFO.
    [Fact]
    public async Task WriterFirst_TheChangeWaitsOutTheWriterTransaction_NotJustItsRead()
    {
        var accountId = await SeedFarmAsync();
        var categoryId = await SeedCategoryAsync(accountId);
        Account snapshot = null!;
        await factory.WithTenantScopeAsync(accountId,
            async db => snapshot = await db.Accounts.AsNoTracking().SingleAsync());

        // The fence: parks both parties without changing anything. Built
        // directly, not via factory.Services — see the #269 comment above.
        var tenantA = new TenantContext();
        tenantA.Resolve(accountId);
        await using var dbA = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenantA);
        await using var transactionA = await dbA.Database.BeginTransactionAsync();
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        var holderPid = await dbA.BackendPidAsync();

        var write = Task.Run(async () =>
        {
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            return await RunHandlerAsync("expense", scope.ServiceProvider,
                accountId, new Seeded(CategoryId: categoryId));
        });
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(write, holderPid),
            "the writer must park on the fence first, so it heads the lock queue");

        var change = Task.Run(async () =>
        {
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            var handler = scope.ServiceProvider.GetRequiredService<UpdateFarmSettingsHandler>();
            return await handler.HandleAsync(ChangeCurrencyCommand(snapshot), CancellationToken.None);
        });
        // minBlockedCount: 2 (#402) — the writer is ALREADY parked at this
        // point, so a plain "is anyone blocked" check would pass instantly
        // without ever proving the change's own FOR UPDATE reached Postgres's
        // wait queue. Requiring the count to reach 2 forces this to observe
        // the change's registration before the fence below is released.
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(change, holderPid, minBlockedCount: 2),
            "the change must queue up behind the same fence");

        // Release the fence without having changed anything. Writer commits
        // first (queue head); the change then sees its row and refuses.
        await transactionA.RollbackAsync();
        var writeError = await write;
        var result = await change;

        Assert.True(writeError is null, $"the writer must commit: {writeError}");
        Assert.True(result.IsFailure, "the change must wait out the writer's WHOLE transaction and refuse");
        Assert.Equal("Account.CurrencyLocked", result.Error.Code);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            Assert.Equal("USD", (await db.Accounts.AsNoTracking().SingleAsync()).DefaultCurrencyCode);
            Assert.Equal("USD", (await db.Expenses.AsNoTracking().SingleAsync()).CurrencyCode);
        });
    }

    // One entry per handler that stamps the account currency onto a row. A
    // handler MISSING its FOR SHARE passes every functional suite — the only
    // thing that catches it is racing it against a held exclusive lock, so
    // each stamping path gets its own turn behind the fence.
    // RecordPaymentHandler is deliberately absent: it stamps the ORDER's
    // snapshotted currency off a row it already holds FOR UPDATE, and an
    // existing order refuses the currency change outright.
    public static TheoryData<string> StampingHandlers => new()
    {
        "expense", "sales-order", "product-create", "product-update",
        "inventory-create", "inventory-update", "purchase"
    };

    [Theory]
    [MemberData(nameof(StampingHandlers))]
    public async Task EveryStampingHandler_SerializesBehindTheCurrencyChange_AndStampsTheNewCurrency(string handlerKey)
    {
        var accountId = await SeedFarmAsync();
        var seeded = await SeedForAsync(handlerKey, accountId);

        // A = the currency change mid-flight: exclusive lock held, new
        // currency written, transaction still open. Built directly, not via
        // factory.Services — see the #269 comment above.
        var tenantA = new TenantContext();
        tenantA.Resolve(accountId);
        await using var dbA = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenantA);
        await using var transactionA = await dbA.Database.BeginTransactionAsync();
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Accounts" SET "DefaultCurrencyCode" = 'JPY', "DefaultCurrencyMinorUnit" = 0, "Version" = "Version" + 1 WHERE "Id" = {accountId}""");
        var holderPid = await dbA.BackendPidAsync();

        // B = the real handler. It must park on the shared lock and, once the
        // change commits, stamp the NEW currency — not the stale one it would
        // have read without the lock.
        var write = Task.Run(async () =>
        {
            using var scopeB = factory.Services.CreateScope();
            scopeB.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            return await RunHandlerAsync(handlerKey, scopeB.ServiceProvider, accountId, seeded);
        });

        var blocked = await factory.WaitUntilDoneOrBlockedAsync(write, holderPid);
        Assert.True(blocked, $"{handlerKey} must park on the account row's lock, not stamp the stale currency");

        await transactionA.CommitAsync();
        var error = await write;

        Assert.True(error is null, $"{handlerKey} must proceed after the change commits, but failed: {error}");
        await factory.WithTenantScopeAsync(accountId, async db =>
            Assert.Equal("JPY", await StampedCurrencyAsync(handlerKey, db, seeded)));
    }

    private sealed record Seeded(Guid CategoryId = default, Guid CustomerId = default,
        Guid GradeId = default, Guid ProductId = default, Guid ItemId = default);

    private async Task<Seeded> SeedForAsync(string handlerKey, Guid accountId)
    {
        var seeded = new Seeded();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            switch (handlerKey)
            {
                case "expense":
                    var categoryId = Guid.NewGuid();
                    db.ExpenseCategories.Add(ExpenseCategory.Create(
                        categoryId, accountId, SeedDefaults.FarmId, $"Cat-{categoryId:N}"[..12]));
                    seeded = seeded with { CategoryId = categoryId };
                    break;
                case "sales-order":
                    var customerId = Guid.NewGuid();
                    db.Customers.Add(Domain.Sales.Customer.Create(customerId, accountId, "Race Buyer", "555-0100"));
                    seeded = seeded with { CustomerId = customerId };
                    break;
                case "product-create":
                case "product-update":
                    var gradeId = Guid.NewGuid();
                    db.EggGrades.Add(Domain.Eggs.EggGrade.Create(
                        gradeId, accountId, SeedDefaults.FarmId, "Race Grade",
                        Domain.Eggs.EggGradeType.Size, 1, isSaleable: true));
                    seeded = seeded with { GradeId = gradeId };
                    if (handlerKey == "product-update")
                    {
                        // Unpriced on purpose: pricing it is the transition
                        // that stamps the currency.
                        var productId = Guid.NewGuid();
                        db.Products.Add(Domain.Catalog.Product.Create(
                            productId, accountId, SeedDefaults.FarmId, "Race Tray",
                            Domain.Catalog.ProductType.Egg, Domain.Catalog.ProductUnit.Tray,
                            defaultPriceMinorUnits: null, "USD", 2, notes: null));
                        db.Set<Domain.Catalog.ProductEggGradeMapping>().Add(
                            Domain.Catalog.ProductEggGradeMapping.Create(
                                Guid.NewGuid(), accountId, productId, gradeId));
                        seeded = seeded with { ProductId = productId };
                    }
                    break;
                case "inventory-update":
                case "purchase":
                    var itemId = Guid.NewGuid();
                    db.InventoryItems.Add(Domain.Inventory.InventoryItem.Create(
                        itemId, accountId, SeedDefaults.FarmId, "Race Feed",
                        Domain.Inventory.InventoryCategory.Feed, "kg", defaultUnitCost: null));
                    seeded = seeded with { ItemId = itemId };
                    break;
            }
            await db.SaveChangesAsync();
        });
        return seeded;
    }

    // Runs the real handler; returns null on success, the error code otherwise.
    private static async Task<string?> RunHandlerAsync(
        string handlerKey, IServiceProvider services, Guid accountId, Seeded seeded)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        switch (handlerKey)
        {
            case "expense":
                var expense = await services.GetRequiredService<CreateExpenseHandler>().HandleAsync(
                    new CreateExpenseCommand(seeded.CategoryId, today, "Raced expense", 34_00, null, null),
                    accountId, CancellationToken.None);
                return expense.IsSuccess ? null : expense.Error.Code;
            case "sales-order":
                var order = await services.GetRequiredService<Application.Features.Sales.CreateSalesOrder.CreateSalesOrderHandler>()
                    .HandleAsync(new Application.Features.Sales.CreateSalesOrder.CreateSalesOrderCommand(
                        seeded.CustomerId, today), accountId, CancellationToken.None);
                return order.IsSuccess ? null : order.Error.Code;
            case "product-create":
                var product = await services.GetRequiredService<Application.Features.Catalog.CreateProduct.CreateProductHandler>()
                    .HandleAsync(new Application.Features.Catalog.CreateProduct.CreateProductCommand(
                        "Raced Dozen", "Egg", "Dozen", 550, seeded.GradeId, null), accountId, CancellationToken.None);
                return product.IsSuccess ? null : product.Error.Code;
            case "product-update":
                var update = await services.GetRequiredService<Application.Features.Catalog.UpdateProduct.UpdateProductHandler>()
                    .HandleAsync(new Application.Features.Catalog.UpdateProduct.UpdateProductCommand(
                        seeded.ProductId, "Race Tray", "Tray", 700, seeded.GradeId, null), CancellationToken.None);
                return update.IsSuccess ? null : update.Error.Code;
            case "inventory-create":
                var item = await services.GetRequiredService<Application.Features.Inventory.CreateInventoryItem.CreateInventoryItemHandler>()
                    .HandleAsync(new Application.Features.Inventory.CreateInventoryItem.CreateInventoryItemCommand(
                        "Raced Grit", "Supplement", "kg", 900), accountId, CancellationToken.None);
                return item.IsSuccess ? null : item.Error.Code;
            case "inventory-update":
                var priced = await services.GetRequiredService<Application.Features.Inventory.UpdateInventoryItem.UpdateInventoryItemHandler>()
                    .HandleAsync(new Application.Features.Inventory.UpdateInventoryItem.UpdateInventoryItemCommand(
                        seeded.ItemId, "Race Feed", "kg", 1200), accountId, CancellationToken.None);
                return priced.IsSuccess ? null : priced.Error.Code;
            case "purchase":
                var purchase = await services.GetRequiredService<Application.Features.Inventory.RecordPurchase.RecordPurchaseHandler>()
                    .HandleAsync(new Application.Features.Inventory.RecordPurchase.RecordPurchaseCommand(
                        seeded.ItemId, today, 25m, 4_500, null, null, null), accountId, CancellationToken.None);
                return purchase.IsSuccess ? null : purchase.Error.Code;
            default:
                throw new ArgumentOutOfRangeException(nameof(handlerKey), handlerKey, null);
        }
    }

    private static Task<string> StampedCurrencyAsync(string handlerKey, AppDbContext db, Seeded seeded) =>
        handlerKey switch
        {
            "expense" => db.Expenses.AsNoTracking().Select(e => e.CurrencyCode).SingleAsync(),
            "sales-order" => db.SalesOrders.AsNoTracking().Select(o => o.TotalAmount.CurrencyCode).SingleAsync(),
            "product-create" => db.Products.AsNoTracking().Select(p => p.CurrencyCode).SingleAsync(),
            "product-update" => db.Products.AsNoTracking().Where(p => p.Id == seeded.ProductId)
                .Select(p => p.CurrencyCode).SingleAsync(),
            "inventory-create" or "inventory-update" => db.InventoryItems.AsNoTracking()
                .Select(i => i.DefaultUnitCost!.CurrencyCode).SingleAsync(),
            "purchase" => db.InventoryLots.AsNoTracking().Select(l => l.UnitCost.CurrencyCode).SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(handlerKey), handlerKey, null)
        };
}
