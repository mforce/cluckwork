namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Flocks.RecordBirdMovement;
using Cluckwork.Application.Features.Catalog.CreateProduct;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Application.Features.Sales.CreateSalesOrder;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// SeedStatus / SeedResult moved to SeedResult.cs (#279) — shared by both this
// seeder and SimulationDataSeeder.

// Dev/demo sample data (#58): runs once on a fresh database, no-op afterwards
// (empty-flock-catalog guard), never resurrects deleted data. Everything goes
// through the real handlers with the tenant resolved to the seeded account, so
// lots, stock, bird movements, and FIFO allocation all exist exactly as if a
// user had clicked them in.
//
// #280/#284: no longer self-gated on Seed:Demo/Seed:Enabled — the ONLY caller
// is the explicit `seed --profile demo` command (Program.cs), which is itself
// the gate (plus the Production DI-registration guard there). A config toggle
// that only prevented *boot*-seeding is meaningless once nothing calls this on
// boot. Authoritative and fail-loud: SeedAsync reports what happened via
// SeedResult (shared with SimulationDataSeeder, #279) instead of swallowing
// every outcome into a silent success.
public sealed class DemoDataSeeder(
    AppDbContext db,
    TenantContext tenant,
    IEggGradeRepository eggGrades,
    CreateFlockHandler createFlock,
    RecordDailyEntryHandler recordEntry,
    SubmitDailyEntryHandler submitEntry,
    RecordBirdMovementHandler recordMovement,
    CreateProductHandler createProduct,
    CreateCustomerHandler createCustomer,
    CreateSalesOrderHandler createOrder,
    AddOrderItemHandler addItem,
    ConfirmSaleHandler confirmSale,
    ILogger<DemoDataSeeder> logger)
{
    public async Task<SeedResult> SeedAsync(CancellationToken ct = default)
    {
        var accountId = SeedDefaults.AccountId;

        // Preflight the base prerequisite (#284 review): demo needs the default
        // account, the Admin role, and the default egg grades (FK dep for the
        // daily-entry grade lines below) that DatabaseSeeder creates on normal
        // boot — but the seed command never runs DatabaseSeeder. Against a
        // migrated-but-never-booted database this used to throw a raw
        // FK/NullReference-shaped exception (or worse, swallow it) instead of
        // telling the operator what to do.
        var missingBaseData = await MissingBaseDataAsync(accountId, ct);
        if (missingBaseData)
        {
            const string message =
                "Demo seed prerequisites missing: the base data (default account, Admin role, default egg " +
                "grades) is not seeded yet. Run the app once against this database with Seed:AdminEmail / " +
                "Seed:AdminPassword set (DatabaseSeeder base-seeds on boot), then re-run `seed --profile demo`.";
            logger.LogError(message);
            return SeedResult.PrerequisitesMissing(message);
        }

        var anyFlocks = await db.Flocks
            .IgnoreQueryFilters()
            .AnyAsync(f => f.AccountId == accountId, ct);
        if (anyFlocks)
        {
            const string message = "Demo seed skipped: flocks already exist.";
            logger.LogInformation(message);
            return SeedResult.AlreadySeeded(message);
        }

        // Handlers and query filters need the tenant, which is unresolved at
        // startup — resolve it to the seeded account for this scope.
        tenant.Resolve(accountId);

        try
        {
            await SeedDemoAsync(accountId, ct);
            const string message = "Demo data seeded.";
            logger.LogInformation(message);
            return SeedResult.Seeded(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo seed failed; removing partial demo data.");
            await CleanupPartialSeedAsync(accountId);
            return SeedResult.Failed($"Demo seed failed: {ex.Message}");
        }
    }

    // Cheap existence checks only — this must run BEFORE tenant.Resolve, so
    // every tenant-scoped query needs IgnoreQueryFilters (same reasoning as the
    // anyFlocks check below and DatabaseSeeder's own startup checks). Roles
    // carry no tenant filter, so db.Roles needs none.
    private async Task<bool> MissingBaseDataAsync(Guid accountId, CancellationToken ct)
    {
        var accountExists = await db.Accounts
            .IgnoreQueryFilters()
            .AnyAsync(a => a.Id == accountId, ct);
        if (!accountExists) return true;

        var adminRoleExists = await db.Roles.AnyAsync(r => r.Name == DatabaseSeeder.AdminRole, ct);
        if (!adminRoleExists) return true;

        var anyGrades = await db.EggGrades
            .IgnoreQueryFilters()
            .AnyAsync(g => g.AccountId == accountId, ct);
        return !anyGrades;
    }

    // The handlers commit step by step (ConfirmSale even opens its own
    // transaction, so one outer transaction can't wrap the whole seed). If a
    // later step fails, committed rows would otherwise trip the empty-catalog
    // guard forever with a half-seeded demo. Cleanup is safe on a fresh
    // database: the flock guard proves the flock-rooted rows are ours, and
    // DatabaseSeeder never writes customers/orders — nothing else can have
    // created them before first startup completes.
    private async Task CleanupPartialSeedAsync(Guid accountId)
    {
        try
        {
            // One transaction: cleanup is all-or-nothing, never half-purged.
            await using var transaction = await db.Database.BeginTransactionAsync();
            // FK-safe order: children before parents.
            await db.SalesOrderItems.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.SalesOrders.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.Customers.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.BirdMovements.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.EggInventoryMovements.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.EggLots.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.DailyEntryGrades.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.DailyEntries.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await db.Flocks.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            logger.LogInformation("Partial demo data removed; next startup will retry the demo seed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo seed cleanup failed; the next startup will skip demo seeding.");
        }
    }

    private async Task SeedDemoAsync(Guid accountId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var grades = (await eggGrades.ListActiveAsync(SeedDefaults.FarmId, ct))
            .Where(g => g.IsSaleable)
            .ToDictionary(g => g.Name, g => g.Id);
        if (grades.Count == 0)
            throw new InvalidOperationException("Demo seed needs the default egg grades.");

        // --- Flocks: two active at different ages + one depleted historical.
        var house1 = Require(await createFlock.HandleAsync(new CreateFlockCommand(
            "House 1 layers", "ISA Brown", today.AddDays(-45 * 7), 500), accountId, ct));
        var house2 = Require(await createFlock.HandleAsync(new CreateFlockCommand(
            "House 2 layers", "Lohmann Brown", today.AddDays(-20 * 7), 400), accountId, ct));
        var oldBatch = Require(await createFlock.HandleAsync(new CreateFlockCommand(
            "2025 batch (sold)", "ISA Brown", today.AddDays(-90 * 7), 450), accountId, ct));

        // Backdated depletion via the domain (the handler stamps "today", which
        // would block the historical entries below).
        var old = await db.Flocks.FirstAsync(f => f.Id == oldBatch, ct);
        Check(old.Deplete(today.AddDays(-30)));
        await db.SaveChangesAsync(ct);

        // --- A week of submitted entries per active flock. Deterministic
        // variation (no Random: reproducible demos). Today stays unrecorded for
        // House 2 so the dashboard shows the "no entry" flag.
        foreach (var (flockId, baseline) in new[] { (house1, 430), (house2, 350) })
        {
            for (var d = 7; d >= 0; d--)
            {
                if (d == 0 && flockId == house2) continue;
                var date = today.AddDays(-d);
                var total = baseline + (d * 7) % 23;
                var cracked = 4 + d % 3;
                var dirty = 2 + d % 2;
                var mortality = d % 3 == 0 ? 1 : 0;
                var sellable = total - cracked - dirty - 1;
                var large = sellable * 55 / 100;
                var medium = sellable * 30 / 100;
                var small = sellable - large - medium;

                var entry = Require(await recordEntry.HandleAsync(new RecordDailyEntryCommand(
                    SeedDefaults.FarmId, SeedDefaults.HouseId, flockId, date,
                    total, cracked, dirty, DiscardedEggs: 1, mortality,
                    [
                        new GradeQuantityDto(grades["Large"], large),
                        new GradeQuantityDto(grades["Medium"], medium),
                        new GradeQuantityDto(grades["Small"], small),
                    ]), accountId, ct));

                // Leave today's House 1 entry as a Draft so both entry states
                // are visible; everything older is submitted (lots + movements).
                if (d > 0)
                    Require(await submitEntry.HandleAsync(entry, accountId, ct));
            }
        }

        // --- A manual cull on the depleted flock's final days.
        Require(await recordMovement.HandleAsync(new RecordBirdMovementCommand(
            oldBatch, today.AddDays(-31), "Cull", 430, "End of lay — sold as spent hens"),
            accountId, ct));

        // --- Customers.
        var mercado = Require(await createCustomer.HandleAsync(new CreateCustomerCommand(
            "Mercado Central", "555-0100", "orders@mercadocentral.example", "12 Market Rd", "Pays cash"),
            accountId, ct));
        var kcc = Require(await createCustomer.HandleAsync(new CreateCustomerCommand(
            "KCC Bakery", "555-0117", null, null, "Weekly standing order"), accountId, ct));
        Require(await createCustomer.HandleAsync(new CreateCustomerCommand(
            "Hotel Paraíso", "555-0142"), accountId, ct));

        // --- Products (#99): sales sell products, not raw grades. Prices are
        // per individual egg (unit Egg → factor 1), preserving the old demo math.
        var largeEggs = Require(await createProduct.HandleAsync(new CreateProductCommand(
            "Large Eggs", "Egg", "Egg", 45, grades["Large"], null), accountId, ct));
        var mediumEggs = Require(await createProduct.HandleAsync(new CreateProductCommand(
            "Medium Eggs", "Egg", "Egg", 38, grades["Medium"], null), accountId, ct));

        // --- Orders: one confirmed (exercises FIFO allocation), one open draft.
        var confirmed = Require(await createOrder.HandleAsync(new CreateSalesOrderCommand(
            mercado, today.AddDays(-1)), accountId, ct));
        Require(await addItem.HandleAsync(new AddOrderItemCommand(
            confirmed, largeEggs, 360, null, null), accountId, ct));
        Require(await addItem.HandleAsync(new AddOrderItemCommand(
            confirmed, mediumEggs, 180, null, null), accountId, ct));
        Check((await confirmSale.HandleAsync(new ConfirmSaleCommand(confirmed), accountId, ct))
            is { IsSuccess: true } ? Result.Success() : Result.Failure(Error.Domain("Demo.Confirm", "confirm failed")));

        var draft = Require(await createOrder.HandleAsync(new CreateSalesOrderCommand(
            kcc, today), accountId, ct));
        Require(await addItem.HandleAsync(new AddOrderItemCommand(
            draft, largeEggs, 240, null, null), accountId, ct));
    }

    private static Guid Require(Result<Guid> result)
    {
        Check(result.IsSuccess ? Result.Success() : Result.Failure(result.Error));
        return result.Value;
    }

    private static void Require(Result<SubmitDailyEntryResponse> result) =>
        Check(result.IsSuccess ? Result.Success() : Result.Failure(result.Error));

    private static void Check(Result result)
    {
        if (result.IsFailure)
            throw new InvalidOperationException($"Demo seed step failed: {result.Error.Code} — {result.Error.Description}");
    }
}
