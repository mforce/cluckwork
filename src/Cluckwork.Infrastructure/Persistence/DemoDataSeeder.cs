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
using Microsoft.AspNetCore.Identity;
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
    CurrentUserContext currentUser,
    UserManager<ApplicationUser> users,
    IAccountUserDirectory directory,
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
        // daily-entry grade lines below). #283 — these are now static
        // reference data baked into the migration itself via raw
        // migrationBuilder.Sql with WHERE NOT EXISTS guards, so this check
        // should never actually fire against a database this process's own
        // MigrateAsync just brought current; it stays as defense-in-depth
        // against a hand-rolled/partially-restored schema.
        var missingBaseData = await MissingBaseDataAsync(accountId, ct);
        if (missingBaseData)
        {
            const string message =
                "Demo seed prerequisites missing: the base data (default account, Admin role, default egg " +
                "grades) is not present. It ships as part of the EF migrations (#283) — run `migrate` (or " +
                "let this command's own migrate-first step apply it) against a current schema, then re-run " +
                "`seed --profile demo`.";
            logger.LogError(message);
            return SeedResult.PrerequisitesMissing(message);
        }

        // #500 — the demo fixture is signed by the account's Owner, so one must
        // exist. This mirrors the prerequisite SimulationDataSeeder has always
        // had, and it is a REAL one: unlike the account/role/grades above, an
        // Owner comes only from `bootstrap-admin` (#283), which `seed` never
        // runs. Checked BEFORE tenant.Resolve, like its neighbours.
        //
        // This deliberately costs the "seed --profile demo needs nothing but a
        // connection string" property that SeedCommandTests used to pin. The
        // trade was made knowingly: a demo fixture exists to be looked at,
        // looking requires a login, and a login requires an Owner — so the
        // prerequisite turns a later surprise into an immediate, clear failure.
        var (owner, disabledOwners) = await FindOwnerAsync(accountId);
        if (owner is null)
        {
            // The remedy DIFFERS by cause, and naming the wrong one strands the
            // operator in a loop: `bootstrap-admin` treats any Owner ROLE ROW as
            // "already provisioned" and exits 0 having done nothing, so telling
            // someone with a disabled Owner to run it sends them back here with
            // the same message. AdminRecoveryService documents that trap; this
            // is the second place it bites.
            var message = disabledOwners > 0
                ? "Demo seed prerequisites missing: the default account's Owner role is held only by DISABLED " +
                  "user(s), so the seeded records would be signed by an account that cannot log in. There is " +
                  "no in-product repair for this state today, so do not go looking for one: `bootstrap-admin` " +
                  "counts Owner role rows without checking DisabledAt and reports 'already provisioned'; " +
                  "`recover-admin` refuses a disabled target; and the Users screen that could re-enable them " +
                  "is Owner-only, which nobody can now reach. Clear BOTH DisabledAt and DisabledBy for that " +
                  "user directly in the database — they describe one fact, and EnableUserAsync always " +
                  "clears them together — then re-run `seed --profile demo`."
                : "Demo seed prerequisites missing: the default account has no user in the Owner role, so the " +
                  "seeded records would have no author. Run `dotnet Cluckwork.Api.dll bootstrap-admin --email " +
                  "<e>` against this database, then re-run `seed --profile demo`.";
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

        // #500 — and the actor, which IAuditWriter now requires. The whole demo
        // fixture is authored by the Owner: a one-person farm is exactly what
        // the demo represents, so every record's History line names them.
        //
        // The roles come from UserManager, never from a `[Roles.Owner]` literal.
        // What is resolved here is an AUTHORIZATION input (FlockScopeGuard reads
        // it), so a literal would fabricate a privilege rather than report one —
        // and it would keep claiming Owner even for a user demoted between the
        // lookup above and this line.
        currentUser.Resolve(owner.Id, owner.Email!, [.. await users.GetRolesAsync(owner)]);

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

    // #500 — the Owner that signs every demo record.
    //
    // UserManager.GetUsersInRoleAsync is NOT account-scoped: it takes a role
    // name and returns Owners across EVERY account, so the AccountId filter
    // here is load-bearing, not decoration. Without it the demo fixture would
    // be attributed to another tenant's Owner and nothing would look wrong —
    // the History line renders the email off the audit row, never a join.
    // (SimulationDataSeeder.MissingBaseDataAsync filters the same way.)
    //
    // DISABLED Owners are excluded, and that filter is as load-bearing as the
    // account one. Disabling a user keeps their Owner role row and only stamps
    // DisabledAt — IdentityProvider says so itself: "a disabled actor retains
    // its Owner ROLE ROW, only authentication is blocked" — so
    // GetUsersInRoleAsync still returns them. Ordering by Id could then pick a
    // disabled principal over a perfectly good active one, and attribute the
    // whole fixture to an account that cannot log in: every History line would
    // name somebody nobody can sign in as, to look at the very fixture they
    // supposedly created. With ONLY a disabled Owner the preflight would pass
    // and the seed report success, which is exactly the looks-fine-but-isn't
    // shape #500 exists to remove.
    //
    // Ordered by Id so the choice is deterministic when an account has several
    // Owners: a fixture whose attribution varies between runs would break the
    // determinism the seeders rest on (#279).
    //
    // Returns the disabled count too, because the CALLER's advice depends on it:
    // "no Owner at all" and "only disabled Owners" need different remedies.
    private async Task<(ApplicationUser? Owner, int DisabledOwners)> FindOwnerAsync(Guid accountId)
    {
        // #532 — scoped at the query. GetUsersInRoleAsync loaded every Owner in
        // every farm and post-filtered in memory: correct while one farm
        // existed, an O(all farms) cross-tenant read once several do.
        var owners = (await directory.FindByAccountRoleAsync(accountId, Roles.Owner)).ToList();

        return (owners.Where(u => u.DisabledAt is null).OrderBy(u => u.Id).FirstOrDefault(),
                owners.Count(u => u.DisabledAt is not null));
    }

    // Cheap existence checks only — this must run BEFORE tenant.Resolve, so
    // every tenant-scoped query needs IgnoreQueryFilters (same reasoning as the
    // anyFlocks check below). Roles carry no tenant filter, so db.Roles needs
    // none.
    private async Task<bool> MissingBaseDataAsync(Guid accountId, CancellationToken ct)
    {
        var accountExists = await db.Accounts
            .IgnoreQueryFilters()
            .AnyAsync(a => a.Id == accountId, ct);
        if (!accountExists) return true;

        var adminRoleExists = await db.Roles.AnyAsync(r => r.Name == Roles.Owner, ct);
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
    // nothing else writes customers/orders into a fresh account before this
    // seeder's first run.
    private async Task CleanupPartialSeedAsync(Guid accountId)
    {
        try
        {
            // #269 — EnableRetryOnFailure forbids BeginTransactionAsync
            // outside database.CreateExecutionStrategy().ExecuteAsync, so the
            // whole cleanup (previously just "one transaction: all-or-
            // nothing, never half-purged") now runs through that: a
            // transient failure reruns the whole delete set against a fresh
            // transaction. That is safe unconditionally here — every
            // statement is a predicate DELETE ("WHERE AccountId = X"), which
            // is naturally idempotent under retry: rerunning it after a
            // rolled-back attempt (nothing committed) or even, in the
            // theoretical ambiguous-commit race, after a prior attempt's
            // DELETE actually landed, just deletes zero additional rows
            // either way. This is a CLI-only, offline command with no client
            // waiting on a response to disambiguate — there is nothing here
            // for an idempotency key to protect against.
            //
            // This is the ONE user-initiated transaction in the codebase that
            // is genuinely replayable, and so the one place that keeps the
            // strategy's retry. Everything else goes through
            // SingleAttemptExecution — see it for why.
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
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
            });
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

        // --- Submitted entries per active flock, deterministic variation (no
        // Random: reproducible demos). House 1 carries ~8 months of history so
        // every per-grade lot list OUT-PAGES the stock drill-down's 50-lot
        // page (#465 — the load-more pager and date filter are exercisable
        // straight from a demo farm); House 2 keeps a single week. Today stays
        // unrecorded for House 2 so the dashboard shows the "no entry" flag.
        foreach (var (flockId, baseline, days) in new[] { (house1, 430, 240), (house2, 350, 7) })
        {
            for (var d = days; d >= 0; d--)
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
        // The catalog is deliberately partial — Small has no product either, so
        // the Stock screen shows a realistic mix of sellable and unlisted grades.
        var largeEggs = Require(await createProduct.HandleAsync(new CreateProductCommand(
            "Large Eggs", "Egg", "Egg", 45, grades["Large"], null), accountId, ct));
        var mediumEggs = Require(await createProduct.HandleAsync(new CreateProductCommand(
            "Medium Eggs", "Egg", "Egg", 38, grades["Medium"], null), accountId, ct));
        // #396: the cracked counter now mints its own lot at submit, so the demo
        // carries a discounted product for it — otherwise the feature reads as
        // stock appearing that nothing can ever sell. Priced below Small.
        Require(await createProduct.HandleAsync(new CreateProductCommand(
            "Cracked Eggs", "Egg", "Egg", 18, grades["Cracked"], "Sold at a discount"), accountId, ct));

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
