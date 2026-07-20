namespace Cluckwork.Infrastructure.Repositories;

using System.Data;
using System.Runtime.CompilerServices;
using Cluckwork.Application.Features.Export;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #95 — flattens every tenant-owned dataset for CSV export. Rows come through
// the global tenant query filters, so an export only ever contains the calling
// account's data. Money is exported as raw minor units + currency columns —
// never converted to decimals (AGENTS.md money rule).
public sealed class ExportQueries(AppDbContext db) : IExportQueries
{
    // Ordered as packed into the full backup. Infra tables (idempotency,
    // durable jobs, refresh tokens) are deliberately absent: they are not
    // account data and would be meaningless — or harmful — in a restore.
    private static readonly string[] DatasetNames =
    [
        "flocks", "bird-movements", "daily-entries", "daily-entry-grades",
        "egg-grades", "egg-lots", "customers", "sales-orders",
        "sales-order-items", "sales-order-allocations", "payments",
        "inventory-items", "inventory-lots", "inventory-movements",
        "feed-usages", "water-usages", "expense-categories", "expenses",
        "audit-events",
    ];

    public IReadOnlyList<string> Datasets => DatasetNames;

    // REPEATABLE READ pins one Postgres snapshot for every query until the
    // transaction disposes (reads only — rollback on dispose is a no-op), so
    // the full backup's CSVs are mutually consistent (codex review of #96).
    public async Task<IAsyncDisposable> BeginConsistentReadAsync(CancellationToken ct = default)
        => await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

    public ExportDataset? GetDataset(string dataset)
        => dataset switch
        {
            "flocks" => Rows(db.Flocks.AsNoTracking()
                    .OrderBy(x => x.PlacementDate).ThenBy(x => x.Id),
                ["id", "farmId", "houseId", "name", "breed", "placementDate",
                 "initialCount", "status", "depletedOn", "archivedOn", "version"],
                x => [x.Id, x.FarmId, x.HouseId, x.Name, x.Breed, x.PlacementDate,
                      x.InitialCount, x.Status, x.DepletedOn, x.ArchivedOn, x.Version]),

            "bird-movements" => Rows(db.BirdMovements.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "flockId", "date", "type", "quantity", "note", "dailyEntryId"],
                x => [x.Id, x.FlockId, x.Date, x.Type, x.Quantity, x.Note, x.DailyEntryId]),

            "daily-entries" => Rows(db.DailyEntries.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "farmId", "houseId", "flockId", "date", "status", "totalEggs",
                 "crackedEggs", "dirtyEggs", "discardedEggs", "mortalityCount",
                 "adjustReason", "adjustedFromJson", "voidReason", "lockedAtUtc", "version"],
                x => [x.Id, x.FarmId, x.HouseId, x.FlockId, x.Date, x.Status, x.TotalEggs,
                      x.CrackedEggs, x.DirtyEggs, x.DiscardedEggs, x.MortalityCount,
                      x.AdjustReason, x.AdjustedFromJson, x.VoidReason, x.LockedAtUtc, x.Version]),

            "daily-entry-grades" => Rows(db.DailyEntryGrades.AsNoTracking()
                    .OrderBy(x => x.DailyEntryId).ThenBy(x => x.Id),
                ["id", "dailyEntryId", "eggGradeId", "quantity"],
                x => [x.Id, x.DailyEntryId, x.EggGradeId, x.Quantity]),

            "egg-grades" => Rows(db.EggGrades.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Id),
                ["id", "farmId", "name", "gradeType", "sortOrder", "isSaleable", "active", "version"],
                x => [x.Id, x.FarmId, x.Name, x.GradeType, x.SortOrder, x.IsSaleable, x.Active, x.Version]),

            "egg-lots" => Rows(db.EggLots.AsNoTracking()
                    .OrderBy(x => x.ProductionDate).ThenBy(x => x.Id),
                ["id", "flockId", "productionDate", "eggGradeId", "quantityProduced",
                 "quantityAvailable", "dailyEntryId", "restrictedUntil", "version"],
                x => [x.Id, x.FlockId, x.ProductionDate, x.EggGradeId, x.QuantityProduced,
                      x.QuantityAvailable, x.DailyEntryId, x.RestrictedUntil, x.Version]),

            "customers" => Rows(db.Customers.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id),
                ["id", "name", "phone", "email", "address", "note"],
                x => [x.Id, x.Name, x.Phone, x.Email, x.Address, x.Note]),

            "sales-orders" => Rows(db.SalesOrders.AsNoTracking()
                    .OrderBy(x => x.OrderDate).ThenBy(x => x.Id),
                ["id", "referenceNumber", "customerId", "status", "orderDate",
                 "totalMinorUnits", "currencyCode", "currencyMinorUnit", "voidReason", "version"],
                x => [x.Id, x.ReferenceNumber, x.CustomerId, x.Status, x.OrderDate,
                      x.TotalAmount.MinorUnits, x.TotalAmount.CurrencyCode,
                      x.TotalAmount.CurrencyMinorUnit, x.VoidReason, x.Version]),

            "sales-order-items" => Rows(db.SalesOrderItems.AsNoTracking()
                    .OrderBy(x => x.SalesOrderId).ThenBy(x => x.Id),
                ["id", "salesOrderId", "productId", "productTypeSnapshot", "eggGradeId",
                 "unit", "baseUnitFactor", "quantity", "quantityBase",
                 "unitPriceMinorUnits", "currencyCode", "currencyMinorUnit"],
                x => [x.Id, x.SalesOrderId, x.ProductId, x.ProductTypeSnapshot, x.EggGradeId,
                      x.Unit, x.BaseUnitFactor, x.Quantity, x.QuantityBase,
                      x.UnitPrice.MinorUnits, x.UnitPrice.CurrencyCode, x.UnitPrice.CurrencyMinorUnit]),

            "sales-order-allocations" => Rows(db.SalesOrderAllocations.AsNoTracking()
                    .OrderBy(x => x.SalesOrderId).ThenBy(x => x.Id),
                ["id", "salesOrderId", "salesOrderItemId", "eggLotId", "quantity", "releasedOnUtc"],
                x => [x.Id, x.SalesOrderId, x.SalesOrderItemId, x.EggLotId, x.Quantity, x.ReleasedOnUtc]),

            "payments" => Rows(db.Payments.AsNoTracking()
                    .OrderBy(x => x.PaymentDate).ThenBy(x => x.Id),
                ["id", "salesOrderId", "customerId", "paymentDate", "amountMinorUnits",
                 "currencyCode", "currencyMinorUnit", "method", "referenceNumber",
                 "note", "voided", "voidReason", "version"],
                x => [x.Id, x.SalesOrderId, x.CustomerId, x.PaymentDate, x.AmountMinorUnits,
                      x.CurrencyCode, x.CurrencyMinorUnit, x.Method, x.ReferenceNumber,
                      x.Note, x.Voided, x.VoidReason, x.Version]),

            "inventory-items" => Rows(db.InventoryItems.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id),
                ["id", "farmId", "name", "category", "unit", "defaultUnitCostMinorUnits",
                 "currencyCode", "currencyMinorUnit", "active", "version"],
                x => [x.Id, x.FarmId, x.Name, x.Category, x.Unit, x.DefaultUnitCost?.MinorUnits,
                      x.DefaultUnitCost?.CurrencyCode, x.DefaultUnitCost?.CurrencyMinorUnit,
                      x.Active, x.Version]),

            "inventory-lots" => Rows(db.InventoryLots.AsNoTracking()
                    .OrderBy(x => x.ReceivedDate).ThenBy(x => x.Id),
                ["id", "inventoryItemId", "receivedDate", "lotNumber", "expiryDate",
                 "quantityReceived", "quantityAvailable", "unitCostMinorUnits",
                 "currencyCode", "currencyMinorUnit", "version"],
                x => [x.Id, x.InventoryItemId, x.ReceivedDate, x.LotNumber, x.ExpiryDate,
                      x.QuantityReceived, x.QuantityAvailable, x.UnitCost.MinorUnits,
                      x.UnitCost.CurrencyCode, x.UnitCost.CurrencyMinorUnit, x.Version]),

            "inventory-movements" => Rows(db.InventoryMovements.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
                ["id", "inventoryItemId", "inventoryLotId", "date", "type", "quantityDelta",
                 "unit", "flockId", "note", "createdAtUtc", "referenceType", "referenceId"],
                x => [x.Id, x.InventoryItemId, x.InventoryLotId, x.Date, x.Type, x.QuantityDelta,
                      x.Unit, x.FlockId, x.Note, x.CreatedAtUtc, x.ReferenceType, x.ReferenceId]),

            "feed-usages" => Rows(db.FeedUsages.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "flockId", "inventoryItemId", "date", "quantity", "unit",
                 "estimatedCostMinorUnits", "currencyCode", "currencyMinorUnit",
                 "dailyEntryId", "note", "createdAtUtc", "version"],
                x => [x.Id, x.FlockId, x.InventoryItemId, x.Date, x.Quantity, x.Unit,
                      x.EstimatedCost.MinorUnits, x.EstimatedCost.CurrencyCode,
                      x.EstimatedCost.CurrencyMinorUnit, x.DailyEntryId, x.Note,
                      x.CreatedAtUtc, x.Version]),

            "water-usages" => Rows(db.WaterUsages.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "flockId", "date", "quantity", "unit", "source", "meterStart",
                 "meterEnd", "note", "dailyEntryId", "createdAtUtc", "version"],
                x => [x.Id, x.FlockId, x.Date, x.Quantity, x.Unit, x.Source, x.MeterStart,
                      x.MeterEnd, x.Note, x.DailyEntryId, x.CreatedAtUtc, x.Version]),

            "expense-categories" => Rows(db.ExpenseCategories.AsNoTracking()
                    .OrderBy(x => x.Name).ThenBy(x => x.Id),
                ["id", "farmId", "name", "active", "version"],
                x => [x.Id, x.FarmId, x.Name, x.Active, x.Version]),

            "expenses" => Rows(db.Expenses.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "farmId", "expenseCategoryId", "date", "description",
                 "amountMinorUnits", "currencyCode", "currencyMinorUnit",
                 "flockId", "note", "version"],
                x => [x.Id, x.FarmId, x.ExpenseCategoryId, x.Date, x.Description,
                      x.AmountMinorUnits, x.CurrencyCode, x.CurrencyMinorUnit,
                      x.FlockId, x.Note, x.Version]),

            "audit-events" => Rows(db.AuditEvents.AsNoTracking()
                    .OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id),
                ["id", "occurredAtUtc", "actorUserId", "actorEmail", "action",
                 "entityType", "entityId", "reason", "detailsJson"],
                x => [x.Id, x.OccurredAtUtc, x.ActorUserId, x.ActorEmail, x.Action,
                      x.EntityType, x.EntityId, x.Reason, x.DetailsJson]),

            _ => null,
        };

    private static ExportDataset Rows<T>(IQueryable<T> query, string[] header, Func<T, object?[]> map)
        => new(header, Enumerate(query, map));

    private static async IAsyncEnumerable<object?[]> Enumerate<T>(
        IQueryable<T> query, Func<T, object?[]> map,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in query.AsAsyncEnumerable().WithCancellation(ct))
            yield return map(item);
    }
}
