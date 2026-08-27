namespace Cluckwork.Infrastructure.Repositories;

using System.Data;
using System.Runtime.CompilerServices;
using Cluckwork.Application.Features.Export;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

// #95 — flattens every tenant-owned dataset for CSV export. Rows come through
// the global tenant query filters, so an export only ever contains the calling
// account's data. Money is exported as raw minor units + currency columns —
// never converted to decimals (AGENTS.md money rule).
public sealed class ExportQueries(AppDbContext db, TenantContext tenant, FlockScope flockScope) : IExportQueries
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
        "egg-inventory-movements", "audit-events",
    ];

    // Named field (rather than referencing the primary-constructor parameter
    // directly below) so the compiler doesn't have to decide whether `db`
    // needs to be captured as hidden state on top of the explicit field
    // assignments here (CS9124).
    private readonly AppDbContext requestDb = db;

    // Defaults to the request-scoped context — retry-enabled, which is fine
    // here: ExportDataset (the single-dataset download) never opens a
    // transaction, so its one query gets EF's normal automatic per-call
    // retry. BeginConsistentReadAsync below swaps this to a SEPARATE,
    // non-retrying context for the duration of a full-backup export; see its
    // comment for why.
    private AppDbContext activeDb = db;

    public IReadOnlyList<string> Datasets => DatasetNames;

    // REPEATABLE READ pins one Postgres snapshot for every query until the
    // transaction disposes (reads only — rollback on dispose is a no-op), so
    // the full backup's CSVs are mutually consistent (codex review of #96).
    //
    // #269 — EnableRetryOnFailure forbids a manual transaction from being
    // touched by anything outside the SAME database.CreateExecutionStrategy()
    // .ExecuteAsync call that opened it — not just the Begin call itself, but
    // every later operation run against it, for as long as it stays open.
    // Tried first: wrap only BeginTransactionAsync in ExecuteAsync and leave
    // the transaction open across the many separate, later query calls
    // WriteZipAsync makes while streaming — this reliably threw "does not
    // support user-initiated transactions" the moment the FIRST row was
    // queried, because by then ExecuteAsync's own call had already returned;
    // there is no way to keep a transaction "inside" ExecuteAsync's
    // protection across multiple separate top-level calls without also
    // wrapping ALL of them in ONE ExecuteAsync region. And that would be
    // actively wrong here regardless: once bytes have reached the client,
    // "retrying" by re-running the whole export from a fresh transaction
    // would interleave a second full set of zip entries into an
    // already-partially-sent download and corrupt it (see #269's PR
    // description). So this snapshot runs on a SEPARATE AppDbContext with NO
    // retry strategy configured at all (Npgsql's default, non-retrying one) —
    // sidestepping the guard entirely rather than fighting it, which is the
    // right outcome anyway: export is READ-ONLY (no ambiguous-commit risk to
    // reason about), and a transient failure mid-stream just fails the
    // download cleanly (WriteZipAsync's own catch / writer.CompleteAsync(ex))
    // for the client to retry as a fresh, side-effect-free GET. The
    // request-scoped `db` (used for the audit-write SaveChanges before this
    // is even called) keeps its retry strategy untouched.
    public async Task<IAsyncDisposable> BeginConsistentReadAsync(CancellationToken ct = default)
    {
        var snapshotDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(requestDb.Database.GetConnectionString())
                .Options,
            tenant, flockScope);
        IDbContextTransaction transaction;
        try
        {
            transaction = await snapshotDb.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        }
        catch
        {
            await snapshotDb.DisposeAsync();
            throw;
        }

        activeDb = snapshotDb;
        return new AsyncDisposableAction(async () =>
        {
            activeDb = requestDb;
            await transaction.DisposeAsync();
            await snapshotDb.DisposeAsync();
        });
    }

    private sealed class AsyncDisposableAction(Func<ValueTask> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => action();
    }

    public ExportDataset? GetDataset(string dataset)
        => dataset switch
        {
            "flocks" => Rows(activeDb.Flocks.AsNoTracking()
                    .OrderBy(x => x.PlacementDate).ThenBy(x => x.Id),
                ["id", "farmId", "houseId", "name", "breed", "placementDate",
                 "initialCount", "status", "depletedOn", "archivedOn", "version"],
                x => [x.Id, x.FarmId, x.HouseId, x.Name, x.Breed, x.PlacementDate,
                      x.InitialCount, x.Status, x.DepletedOn, x.ArchivedOn, x.Version]),

            "bird-movements" => Rows(activeDb.BirdMovements.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "flockId", "date", "type", "quantity", "note", "dailyEntryId"],
                x => [x.Id, x.FlockId, x.Date, x.Type, x.Quantity, x.Note, x.DailyEntryId]),

            "daily-entries" => Rows(activeDb.DailyEntries.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                // #396 — the two snapshot ids ride next to the counters they
                // explain. Without them an export records that a day had 40
                // cracked eggs but not whether those became saleable stock or a
                // loss, and that is no longer derivable from the current
                // catalog: the whole point of the snapshot is that the catalog
                // may have changed since.
                ["id", "farmId", "houseId", "flockId", "date", "status", "totalEggs",
                 "crackedEggs", "dirtyEggs", "discardedEggs", "mortalityCount",
                 "crackedGradeId", "dirtyGradeId",
                 "adjustReason", "adjustedFromJson", "voidReason", "lockedAtUtc", "version"],
                x => [x.Id, x.FarmId, x.HouseId, x.FlockId, x.Date, x.Status, x.TotalEggs,
                      x.CrackedEggs, x.DirtyEggs, x.DiscardedEggs, x.MortalityCount,
                      x.CrackedGradeId, x.DirtyGradeId,
                      x.AdjustReason, x.AdjustedFromJson, x.VoidReason, x.LockedAtUtc, x.Version]),

            "daily-entry-grades" => Rows(activeDb.DailyEntryGrades.AsNoTracking()
                    .OrderBy(x => x.DailyEntryId).ThenBy(x => x.Id),
                ["id", "dailyEntryId", "eggGradeId", "quantity"],
                x => [x.Id, x.DailyEntryId, x.EggGradeId, x.Quantity]),

            "egg-grades" => Rows(activeDb.EggGrades.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Id),
                // #396 — dailyEntryKind is what makes the snapshot ids above
                // interpretable: it is the only field saying WHICH counter a
                // grade serves, and unlike the name it cannot be edited.
                ["id", "farmId", "name", "gradeType", "sortOrder", "isSaleable",
                 "dailyEntryKind", "active", "version"],
                x => [x.Id, x.FarmId, x.Name, x.GradeType, x.SortOrder, x.IsSaleable,
                      x.DailyEntryKind, x.Active, x.Version]),

            "egg-lots" => Rows(activeDb.EggLots.AsNoTracking()
                    .OrderBy(x => x.ProductionDate).ThenBy(x => x.Id),
                ["id", "flockId", "productionDate", "eggGradeId", "quantityProduced",
                 "quantityAvailable", "dailyEntryId", "restrictedUntil", "version"],
                x => [x.Id, x.FlockId, x.ProductionDate, x.EggGradeId, x.QuantityProduced,
                      x.QuantityAvailable, x.DailyEntryId, x.RestrictedUntil, x.Version]),

            "customers" => Rows(activeDb.Customers.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id),
                ["id", "name", "phone", "email", "address", "note"],
                x => [x.Id, x.Name, x.Phone, x.Email, x.Address, x.Note]),

            "sales-orders" => Rows(activeDb.SalesOrders.AsNoTracking()
                    .OrderBy(x => x.OrderDate).ThenBy(x => x.Id),
                ["id", "referenceNumber", "customerId", "status", "orderDate",
                 "totalMinorUnits", "currencyCode", "currencyMinorUnit", "voidReason", "version"],
                x => [x.Id, x.ReferenceNumber, x.CustomerId, x.Status, x.OrderDate,
                      x.TotalAmount.MinorUnits, x.TotalAmount.CurrencyCode,
                      x.TotalAmount.CurrencyMinorUnit, x.VoidReason, x.Version]),

            "sales-order-items" => Rows(activeDb.SalesOrderItems.AsNoTracking()
                    .OrderBy(x => x.SalesOrderId).ThenBy(x => x.Id),
                ["id", "salesOrderId", "productId", "productTypeSnapshot", "eggGradeId",
                 "unit", "baseUnitFactor", "quantity", "quantityBase",
                 "unitPriceMinorUnits", "currencyCode", "currencyMinorUnit"],
                x => [x.Id, x.SalesOrderId, x.ProductId, x.ProductTypeSnapshot, x.EggGradeId,
                      x.Unit, x.BaseUnitFactor, x.Quantity, x.QuantityBase,
                      x.UnitPrice.MinorUnits, x.UnitPrice.CurrencyCode, x.UnitPrice.CurrencyMinorUnit]),

            "sales-order-allocations" => Rows(activeDb.SalesOrderAllocations.AsNoTracking()
                    .OrderBy(x => x.SalesOrderId).ThenBy(x => x.Id),
                ["id", "salesOrderId", "salesOrderItemId", "eggLotId", "quantity", "releasedOnUtc"],
                x => [x.Id, x.SalesOrderId, x.SalesOrderItemId, x.EggLotId, x.Quantity, x.ReleasedOnUtc]),

            "payments" => Rows(activeDb.Payments.AsNoTracking()
                    .OrderBy(x => x.PaymentDate).ThenBy(x => x.Id),
                ["id", "salesOrderId", "customerId", "paymentDate", "amountMinorUnits",
                 "currencyCode", "currencyMinorUnit", "method", "referenceNumber",
                 "note", "voided", "voidReason", "version"],
                x => [x.Id, x.SalesOrderId, x.CustomerId, x.PaymentDate, x.AmountMinorUnits,
                      x.CurrencyCode, x.CurrencyMinorUnit, x.Method, x.ReferenceNumber,
                      x.Note, x.Voided, x.VoidReason, x.Version]),

            "inventory-items" => Rows(activeDb.InventoryItems.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id),
                ["id", "farmId", "name", "category", "unit", "defaultUnitCostMinorUnits",
                 "currencyCode", "currencyMinorUnit", "active", "version"],
                x => [x.Id, x.FarmId, x.Name, x.Category, x.Unit, x.DefaultUnitCost?.MinorUnits,
                      x.DefaultUnitCost?.CurrencyCode, x.DefaultUnitCost?.CurrencyMinorUnit,
                      x.Active, x.Version]),

            "inventory-lots" => Rows(activeDb.InventoryLots.AsNoTracking()
                    .OrderBy(x => x.ReceivedDate).ThenBy(x => x.Id),
                ["id", "inventoryItemId", "receivedDate", "lotNumber", "expiryDate",
                 "quantityReceived", "quantityAvailable", "unitCostMinorUnits",
                 "currencyCode", "currencyMinorUnit", "version"],
                x => [x.Id, x.InventoryItemId, x.ReceivedDate, x.LotNumber, x.ExpiryDate,
                      x.QuantityReceived, x.QuantityAvailable, x.UnitCost.MinorUnits,
                      x.UnitCost.CurrencyCode, x.UnitCost.CurrencyMinorUnit, x.Version]),

            "inventory-movements" => Rows(activeDb.InventoryMovements.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
                ["id", "inventoryItemId", "inventoryLotId", "date", "type", "quantityDelta",
                 "unit", "flockId", "note", "createdAtUtc", "referenceType", "referenceId"],
                x => [x.Id, x.InventoryItemId, x.InventoryLotId, x.Date, x.Type, x.QuantityDelta,
                      x.Unit, x.FlockId, x.Note, x.CreatedAtUtc, x.ReferenceType, x.ReferenceId]),

            "feed-usages" => Rows(activeDb.FeedUsages.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "flockId", "inventoryItemId", "date", "quantity", "unit",
                 "estimatedCostMinorUnits", "currencyCode", "currencyMinorUnit",
                 "dailyEntryId", "note", "createdAtUtc", "version"],
                x => [x.Id, x.FlockId, x.InventoryItemId, x.Date, x.Quantity, x.Unit,
                      x.EstimatedCost.MinorUnits, x.EstimatedCost.CurrencyCode,
                      x.EstimatedCost.CurrencyMinorUnit, x.DailyEntryId, x.Note,
                      x.CreatedAtUtc, x.Version]),

            "water-usages" => Rows(activeDb.WaterUsages.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "flockId", "date", "quantity", "unit", "source", "meterStart",
                 "meterEnd", "note", "dailyEntryId", "createdAtUtc", "version"],
                x => [x.Id, x.FlockId, x.Date, x.Quantity, x.Unit, x.Source, x.MeterStart,
                      x.MeterEnd, x.Note, x.DailyEntryId, x.CreatedAtUtc, x.Version]),

            "expense-categories" => Rows(activeDb.ExpenseCategories.AsNoTracking()
                    .OrderBy(x => x.Name).ThenBy(x => x.Id),
                ["id", "farmId", "name", "active", "version"],
                x => [x.Id, x.FarmId, x.Name, x.Active, x.Version]),

            "expenses" => Rows(activeDb.Expenses.AsNoTracking()
                    .OrderBy(x => x.Date).ThenBy(x => x.Id),
                ["id", "farmId", "expenseCategoryId", "date", "description",
                 "amountMinorUnits", "currencyCode", "currencyMinorUnit",
                 "flockId", "note", "version"],
                x => [x.Id, x.FarmId, x.ExpenseCategoryId, x.Date, x.Description,
                      x.AmountMinorUnits, x.CurrencyCode, x.CurrencyMinorUnit,
                      x.FlockId, x.Note, x.Version]),

            "egg-inventory-movements" => Rows(activeDb.EggInventoryMovements.AsNoTracking()
                    .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
                ["id", "eggLotId", "movementType", "quantityDelta",
                 "referenceType", "referenceId", "reason", "createdAtUtc"],
                x => [x.Id, x.EggLotId, x.MovementType, x.QuantityDelta,
                      x.ReferenceType, x.ReferenceId, x.Reason, x.CreatedAtUtc]),

            "audit-events" => Rows(activeDb.AuditEvents.AsNoTracking()
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
