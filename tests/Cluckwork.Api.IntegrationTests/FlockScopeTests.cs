namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.Inventory.RecordFeedUsage;
using Cluckwork.Application.Features.Inventory.RecordWaterUsage;
using Cluckwork.Application.Features.Reports;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #388 — the read scoping itself (INV-1): a Worker scoped to one flock sees
// only that flock's rows + farm-wide rows; unassigned flock detail is 404
// (symmetric filtering — the row simply is not there, not 403).
[Collection(IntegrationCollection.Name)]
public sealed class FlockScopeTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    // Fixture: one farm; flocks A and B; an Owner; a Worker assigned to flock
    // A only; daily entries + water rows on EACH flock; a farm-wide expense and
    // a flock-B expense.
    private async Task<Fixture> SeedAsync()
    {
        var ownerEmail = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var gradeId = grades["Large"];

        // Owner: seeds the data and assigns the worker.
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail));

        // Daily entry + water row on EACH flock (owner-seeded). Each response
        // is pinned so an absent B row means filtering, not seed failure (#388).
        var entryAResponse = await owner.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId = flockA, date = Today,
            totalEggs = 50, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0, grades = new[] { new { eggGradeId = gradeId, quantity = 50 } }
        });
        Assert.Equal(HttpStatusCode.Created, entryAResponse.StatusCode);
        var entryAId = (await entryAResponse.Content.ReadFromJsonAsync<RecordedDto>())!.Id;

        var entryBResponse = await owner.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId = flockB, date = Today,
            totalEggs = 60, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0, grades = new[] { new { eggGradeId = gradeId, quantity = 60 } }
        });
        Assert.Equal(HttpStatusCode.Created, entryBResponse.StatusCode);
        var entryBId = (await entryBResponse.Content.ReadFromJsonAsync<RecordedDto>())!.Id;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(), new
        {
            flockId = flockA, date = Today, quantity = 10.5m, unit = "L", source = "Municipal"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(), new
        {
            flockId = flockB, date = Today, quantity = 12.5m, unit = "L", source = "Municipal"
        })).StatusCode);

        // Expense category + expenses (repository-seeded; direct flock allocation).
        var categoryId = Guid.NewGuid();
        var farmWideExpenseId = Guid.NewGuid();
        var flockBExpenseId = Guid.NewGuid();
        var flockAExpenseId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(Cluckwork.Domain.Expenses.ExpenseCategory.Create(
                categoryId, accountId, farmId, "Feed"));
            // Farm-wide (FlockId null).
            db.Expenses.Add(Cluckwork.Domain.Expenses.Expense.Create(
                farmWideExpenseId, accountId, farmId, categoryId, Today,
                "Farm-wide expense", 1000, "USD", 2, flockId: null));
            // Flock-B expense.
            db.Expenses.Add(Cluckwork.Domain.Expenses.Expense.Create(
                flockBExpenseId, accountId, farmId, categoryId, Today,
                "B expense", 2000, "USD", 2, flockId: flockB));
            // Flock-A expense.
            db.Expenses.Add(Cluckwork.Domain.Expenses.Expense.Create(
                flockAExpenseId, accountId, farmId, categoryId, Today,
                "A expense", 3000, "USD", 2, flockId: flockA));
            await db.SaveChangesAsync();
        });

        // Worker assigned to flock A only.
        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        var workerId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerEmail).Id;
        Assert.Equal(HttpStatusCode.Created,
            (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        var managerEmail = $"m-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, managerEmail, Roles.Manager);
        var manager = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(managerEmail));

        return new Fixture(
            accountId, farmId, flockA, flockB, gradeId, entryAId, entryBId,
            worker, owner, manager, workerId);
    }

    // INV-1 — the core bug from #388's title.
    [Fact]
    public async Task ScopedWorker_FlocksList_SeesOnlyAssignedFlock()
    {
        var fix = await SeedAsync();
        var response = await fix.Worker.GetAsync("/api/v1/flocks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flocks = await response.Content.ReadFromJsonAsync<List<FlockRow>>();
        Assert.NotNull(flocks);
        Assert.Contains(flocks, f => f.Id == fix.FlockA);
        Assert.DoesNotContain(flocks, f => f.Id == fix.FlockB);
    }

    // Symmetric filtering (settled decision 2): 404, not 403.
    [Fact]
    public async Task ScopedWorker_UnassignedFlockDetail_Returns404()
    {
        var fix = await SeedAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await fix.Worker.GetAsync($"/api/v1/flocks/{fix.FlockB}")).StatusCode);
    }

    // Positive control (INV-1 inverse): the assigned flock is still readable.
    [Fact]
    public async Task ScopedWorker_AssignedFlockDetail_Returns200()
    {
        var fix = await SeedAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await fix.Worker.GetAsync($"/api/v1/flocks/{fix.FlockA}")).StatusCode);
    }

    // INV-2 — elevated roles unrestricted.
    [Fact]
    public async Task Owner_FlocksList_SeesAllFlocks()
    {
        var fix = await SeedAsync();
        var response = await fix.Owner.GetAsync("/api/v1/flocks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flocks = await response.Content.ReadFromJsonAsync<List<FlockRow>>();
        Assert.NotNull(flocks);
        Assert.Contains(flocks, f => f.Id == fix.FlockA);
        Assert.Contains(flocks, f => f.Id == fix.FlockB);
    }

    [Fact]
    public async Task Manager_FlocksList_SeesAllFlocks()
    {
        var fix = await SeedAsync();
        var response = await fix.Manager.GetAsync("/api/v1/flocks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flocks = await response.Content.ReadFromJsonAsync<List<FlockRow>>();
        Assert.NotNull(flocks);
        Assert.Contains(flocks, f => f.Id == fix.FlockA);
        Assert.Contains(flocks, f => f.Id == fix.FlockB);
    }

    // Child-row scoping through Worker-reachable reads.
    [Fact]
    public async Task ScopedWorker_ChildRows_AreScoped()
    {
        var fix = await SeedAsync();

        // Daily entries: every row's flockId == flock A.
        var entries = await (await fix.Worker.GetAsync("/api/v1/daily-entries"))
            .Content.ReadFromJsonAsync<List<DailyEntryRow>>();
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(fix.FlockA, e.FlockId));

        // Flock movements: assigned flock OK, unassigned 404.
        Assert.Equal(HttpStatusCode.OK,
            (await fix.Worker.GetAsync($"/api/v1/flocks/{fix.FlockA}/movements")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fix.Worker.GetAsync($"/api/v1/flocks/{fix.FlockB}/movements")).StatusCode);

        // Water usage: every row's flockId == flock A.
        var water = await (await fix.Worker.GetAsync("/api/v1/water-usage"))
            .Content.ReadFromJsonAsync<List<WaterUsageRow>>();
        Assert.NotNull(water);
        Assert.NotEmpty(water);
        Assert.All(water, w => Assert.Equal(fix.FlockA, w.FlockId));
    }

    // #613 — DailyEntryGrade has no scalar FlockId. The Worker-readable
    // production report must correlate grade rows through the filtered
    // DailyEntry parent; otherwise the headline is scoped but By grade leaks B.
    [Fact]
    public async Task ScopedWorker_ProductionGradeTotals_UseFilteredDailyEntryParent()
    {
        var fix = await SeedAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await fix.Owner.PostWithKeyAsync(
                $"/api/v1/daily-entries/{fix.EntryAId}/submit", Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fix.Owner.PostWithKeyAsync(
                $"/api/v1/daily-entries/{fix.EntryBId}/submit", Guid.NewGuid().ToString())).StatusCode);

        var response = await fix.Worker.GetAsync(
            $"/api/v1/reports/production?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ProductionReport>();

        Assert.NotNull(report);
        Assert.Equal(50, report.TotalEggs);
        var grade = Assert.Single(report.GradeTotals);
        Assert.Equal(fix.GradeId, grade.EggGradeId);
        Assert.Equal(50, grade.Quantity);
    }

    // The Expense filter: no Worker-reachable HTTP surface (AdminOnly, #87),
    // so assert at the repository layer with a hand-built scoped context.
    [Fact]
    public async Task ExpenseFilter_FarmWideVisible_UnassignedExcluded()
    {
        var fix = await SeedAsync();

        var scope = new FlockScope();
        scope.Resolve(false, [fix.FlockA]);

        var tenant = new TenantContext();
        tenant.Resolve(fix.AccountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        await using var db = new AppDbContext(options, tenant, scope);

        var expenses = await db.Expenses.AsNoTracking().ToListAsync();
        Assert.Contains(expenses, e => e.FlockId is null);      // farm-wide visible
        Assert.Contains(expenses, e => e.FlockId == fix.FlockA); // assigned flock visible
        Assert.DoesNotContain(expenses, e => e.FlockId == fix.FlockB); // unassigned excluded
    }

    // #388 — one direct-EF fact isolates every combined query filter. Each
    // entity has an exact assigned-row positive control and an exact unassigned
    // negative row. Nullable flock links additionally prove farm-wide rows stay
    // visible. Direct DbSet queries avoid parent-filter masking (especially
    // BirdMovement, whose HTTP endpoint checks Flock first).
    [Fact]
    public async Task AllEightCombinedFilters_AssignedPresent_UnassignedAbsent_FarmWideVisible()
    {
        var fix = await SeedAsync();

        var lotAId = Guid.NewGuid();
        var lotBId = Guid.NewGuid();
        var birdAId = Guid.NewGuid();
        var birdBId = Guid.NewGuid();
        var feedAId = Guid.NewGuid();
        var feedBId = Guid.NewGuid();
        Guid inventoryAId = default;
        Guid inventoryBId = default;
        Guid inventoryFarmWideId = default;
        Guid dailyAId = default;
        Guid dailyBId = default;
        Guid waterAId = default;
        Guid waterBId = default;
        Guid expenseAId = default;
        Guid expenseBId = default;
        Guid expenseFarmWideId = default;

        await factory.WithTenantScopeAsync(fix.AccountId, async db =>
        {
            var itemId = Guid.NewGuid();
            db.InventoryItems.Add(InventoryItem.Create(
                itemId, fix.AccountId, fix.FarmId, "Filter guard feed",
                InventoryCategory.Feed, "kg", Money.Zero("USD")));

            db.EggLots.Add(EggLot.Create(
                lotAId, fix.AccountId, fix.FlockA, Today, fix.GradeId, 11));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), fix.AccountId, lotAId, EggMovementType.Production,
                11, "FilterGuard", Guid.NewGuid(), DateTimeOffset.UtcNow));
            db.EggLots.Add(EggLot.Create(
                lotBId, fix.AccountId, fix.FlockB, Today, fix.GradeId, 12));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), fix.AccountId, lotBId, EggMovementType.Production,
                12, "FilterGuard", Guid.NewGuid(), DateTimeOffset.UtcNow));

            db.BirdMovements.Add(BirdMovement.Create(
                birdAId, fix.AccountId, fix.FlockA, Today,
                BirdMovementType.Adjustment, 1, "filter A"));
            db.BirdMovements.Add(BirdMovement.Create(
                birdBId, fix.AccountId, fix.FlockB, Today,
                BirdMovementType.Adjustment, 1, "filter B"));

            var inventoryA = InventoryMovement.Create(
                fix.AccountId, itemId, inventoryLotId: null, Today,
                InventoryMovementType.Adjustment, 1m, "kg", DateTime.UtcNow,
                flockId: fix.FlockA, note: "filter A");
            var inventoryB = InventoryMovement.Create(
                fix.AccountId, itemId, inventoryLotId: null, Today,
                InventoryMovementType.Adjustment, 1m, "kg", DateTime.UtcNow,
                flockId: fix.FlockB, note: "filter B");
            var inventoryFarmWide = InventoryMovement.Create(
                fix.AccountId, itemId, inventoryLotId: null, Today,
                InventoryMovementType.Adjustment, 1m, "kg", DateTime.UtcNow,
                flockId: null, note: "filter farm-wide");
            inventoryAId = inventoryA.Id;
            inventoryBId = inventoryB.Id;
            inventoryFarmWideId = inventoryFarmWide.Id;
            db.InventoryMovements.AddRange(inventoryA, inventoryB, inventoryFarmWide);

            db.FeedUsages.Add(FeedUsage.Create(
                feedAId, fix.AccountId, fix.FlockA, itemId, Today,
                1m, "kg", Money.Zero("USD"), DateTime.UtcNow));
            db.FeedUsages.Add(FeedUsage.Create(
                feedBId, fix.AccountId, fix.FlockB, itemId, Today,
                1m, "kg", Money.Zero("USD"), DateTime.UtcNow));

            await db.SaveChangesAsync();

            dailyAId = await db.DailyEntries.Where(e => e.FlockId == fix.FlockA)
                .Select(e => e.Id).SingleAsync();
            dailyBId = await db.DailyEntries.Where(e => e.FlockId == fix.FlockB)
                .Select(e => e.Id).SingleAsync();
            waterAId = await db.WaterUsages.Where(e => e.FlockId == fix.FlockA)
                .Select(e => e.Id).SingleAsync();
            waterBId = await db.WaterUsages.Where(e => e.FlockId == fix.FlockB)
                .Select(e => e.Id).SingleAsync();
            expenseAId = await db.Expenses.Where(e => e.FlockId == fix.FlockA)
                .Select(e => e.Id).SingleAsync();
            expenseBId = await db.Expenses.Where(e => e.FlockId == fix.FlockB)
                .Select(e => e.Id).SingleAsync();
            expenseFarmWideId = await db.Expenses.Where(e => e.FlockId == null)
                .Select(e => e.Id).SingleAsync();
        });

        var scope = new FlockScope();
        scope.Resolve(false, [fix.FlockA]);
        var tenant = new TenantContext();
        tenant.Resolve(fix.AccountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        await using var db = new AppDbContext(options, tenant, scope);

        var flockIds = await db.Flocks.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(fix.FlockA, flockIds);
        Assert.DoesNotContain(fix.FlockB, flockIds);

        var dailyIds = await db.DailyEntries.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(dailyAId, dailyIds);
        Assert.DoesNotContain(dailyBId, dailyIds);

        var lotIds = await db.EggLots.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(lotAId, lotIds);
        Assert.DoesNotContain(lotBId, lotIds);

        var birdIds = await db.BirdMovements.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(birdAId, birdIds);
        Assert.DoesNotContain(birdBId, birdIds);

        var inventoryIds = await db.InventoryMovements.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(inventoryAId, inventoryIds);
        Assert.Contains(inventoryFarmWideId, inventoryIds);
        Assert.DoesNotContain(inventoryBId, inventoryIds);

        var feedIds = await db.FeedUsages.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(feedAId, feedIds);
        Assert.DoesNotContain(feedBId, feedIds);

        var waterIds = await db.WaterUsages.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(waterAId, waterIds);
        Assert.DoesNotContain(waterBId, waterIds);

        var expenseIds = await db.Expenses.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(expenseAId, expenseIds);
        Assert.Contains(expenseFarmWideId, expenseIds);
        Assert.DoesNotContain(expenseBId, expenseIds);
    }

    // #388 — these five filters were rewritten from tenant-only to combined
    // tenant+flock expressions. Use an UNRESTRICTED flock scope so the flock
    // disjunct is true for every row: only AccountId can exclude the foreign
    // fixtures. Each entity has an exact own-row positive control and an exact
    // foreign-row negative control, making tenant-conjunct mutations causal.
    [Fact]
    public async Task FiveRewrittenFilters_TenantConjunctExcludesForeignRows_WhenFlockScopeUnrestricted()
    {
        var fix = await SeedAsync();
        var ownBirdId = Guid.NewGuid();
        var ownFeedId = Guid.NewGuid();
        var ownWaterId = Guid.NewGuid();
        var ownExpenseId = Guid.NewGuid();
        Guid ownInventoryId = default;

        await factory.WithTenantScopeAsync(fix.AccountId, async db =>
        {
            var itemId = Guid.NewGuid();
            var categoryId = Guid.NewGuid();
            db.InventoryItems.Add(InventoryItem.Create(
                itemId, fix.AccountId, fix.FarmId, "Own tenant guard feed",
                InventoryCategory.Feed, "kg", Money.Zero("USD")));
            db.ExpenseCategories.Add(Cluckwork.Domain.Expenses.ExpenseCategory.Create(
                categoryId, fix.AccountId, fix.FarmId, "Own tenant guard expense"));
            db.BirdMovements.Add(BirdMovement.Create(
                ownBirdId, fix.AccountId, fix.FlockA, Today,
                BirdMovementType.Adjustment, 1, "own tenant guard"));
            var inventory = InventoryMovement.Create(
                fix.AccountId, itemId, inventoryLotId: null, Today,
                InventoryMovementType.Adjustment, 1m, "kg", DateTime.UtcNow,
                flockId: fix.FlockA, note: "own tenant guard");
            ownInventoryId = inventory.Id;
            db.InventoryMovements.Add(inventory);
            db.FeedUsages.Add(FeedUsage.Create(
                ownFeedId, fix.AccountId, fix.FlockA, itemId, Today,
                1m, "kg", Money.Zero("USD"), DateTime.UtcNow));
            db.WaterUsages.Add(WaterUsage.Create(
                ownWaterId, fix.AccountId, fix.FlockA, Today,
                1m, "L", WaterSource.Municipal,
                meterStart: null, meterEnd: null, DateTime.UtcNow));
            db.Expenses.Add(Cluckwork.Domain.Expenses.Expense.Create(
                ownExpenseId, fix.AccountId, fix.FarmId, categoryId, Today,
                "Own tenant guard expense", 100, "USD", 2, flockId: fix.FlockA));
            await db.SaveChangesAsync();
        });

        var foreignOwner = $"foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccountId = await factory.SeedAccountWithUserAsync(foreignOwner);
        var foreignFarmId = Guid.NewGuid();
        var foreignFlockId = await factory.SeedFlockAsync(foreignAccountId, foreignFarmId);
        var foreignBirdId = Guid.NewGuid();
        var foreignFeedId = Guid.NewGuid();
        var foreignWaterId = Guid.NewGuid();
        var foreignExpenseId = Guid.NewGuid();
        Guid foreignInventoryId = default;

        await factory.WithTenantScopeAsync(foreignAccountId, async db =>
        {
            var itemId = Guid.NewGuid();
            var categoryId = Guid.NewGuid();
            db.InventoryItems.Add(InventoryItem.Create(
                itemId, foreignAccountId, foreignFarmId, "Foreign tenant guard feed",
                InventoryCategory.Feed, "kg", Money.Zero("USD")));
            db.ExpenseCategories.Add(Cluckwork.Domain.Expenses.ExpenseCategory.Create(
                categoryId, foreignAccountId, foreignFarmId, "Foreign tenant guard expense"));
            db.BirdMovements.Add(BirdMovement.Create(
                foreignBirdId, foreignAccountId, foreignFlockId, Today,
                BirdMovementType.Adjustment, 1, "foreign tenant guard"));
            var inventory = InventoryMovement.Create(
                foreignAccountId, itemId, inventoryLotId: null, Today,
                InventoryMovementType.Adjustment, 1m, "kg", DateTime.UtcNow,
                flockId: foreignFlockId, note: "foreign tenant guard");
            foreignInventoryId = inventory.Id;
            db.InventoryMovements.Add(inventory);
            db.FeedUsages.Add(FeedUsage.Create(
                foreignFeedId, foreignAccountId, foreignFlockId, itemId, Today,
                1m, "kg", Money.Zero("USD"), DateTime.UtcNow));
            db.WaterUsages.Add(WaterUsage.Create(
                foreignWaterId, foreignAccountId, foreignFlockId, Today,
                1m, "L", WaterSource.Municipal,
                meterStart: null, meterEnd: null, DateTime.UtcNow));
            db.Expenses.Add(Cluckwork.Domain.Expenses.Expense.Create(
                foreignExpenseId, foreignAccountId, foreignFarmId, categoryId, Today,
                "Foreign tenant guard expense", 100, "USD", 2,
                flockId: foreignFlockId));
            await db.SaveChangesAsync();
        });

        var tenant = new TenantContext();
        tenant.Resolve(fix.AccountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        // Fresh FlockScope is deliberately Unrestricted: the tenant conjunct is
        // the only rejecting layer for the foreign fixtures in this fact.
        await using var db = new AppDbContext(options, tenant, new FlockScope());

        var birdIds = await db.BirdMovements.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(ownBirdId, birdIds);
        Assert.DoesNotContain(foreignBirdId, birdIds);

        var inventoryIds = await db.InventoryMovements.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(ownInventoryId, inventoryIds);
        Assert.DoesNotContain(foreignInventoryId, inventoryIds);

        var feedIds = await db.FeedUsages.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(ownFeedId, feedIds);
        Assert.DoesNotContain(foreignFeedId, feedIds);

        var waterIds = await db.WaterUsages.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(ownWaterId, waterIds);
        Assert.DoesNotContain(foreignWaterId, waterIds);

        var expenseIds = await db.Expenses.Select(e => e.Id).ToHashSetAsync();
        Assert.Contains(ownExpenseId, expenseIds);
        Assert.DoesNotContain(foreignExpenseId, expenseIds);
    }

    // #388 — EggInventoryMovement has no FlockId, so the Worker-reachable
    // movement-ledger endpoint is protected by a filtered EggLot parent lookup.
    // Assigned positive + unassigned 404 prove that deleting the parent gate
    // cannot expose tenant-owned movement rows from another flock.
    [Fact]
    public async Task ScopedWorker_LotMovementLedger_UsesFilteredParentGate()
    {
        var fix = await SeedAsync();
        var lotAId = Guid.NewGuid();
        var lotBId = Guid.NewGuid();
        var movementAId = Guid.NewGuid();
        var movementBId = Guid.NewGuid();

        await factory.WithTenantScopeAsync(fix.AccountId, async db =>
        {
            db.EggLots.Add(EggLot.Create(
                lotAId, fix.AccountId, fix.FlockA, Today, fix.GradeId, 10));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                movementAId, fix.AccountId, lotAId, EggMovementType.Production,
                10, "ParentGate", Guid.NewGuid(), DateTimeOffset.UtcNow));
            db.EggLots.Add(EggLot.Create(
                lotBId, fix.AccountId, fix.FlockB, Today, fix.GradeId, 10));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                movementBId, fix.AccountId, lotBId, EggMovementType.Production,
                10, "ParentGate", Guid.NewGuid(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        var assigned = await fix.Worker.GetAsync(
            $"/api/v1/stock/lots/{lotAId}/movements");
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var assignedRows = await assigned.Content.ReadFromJsonAsync<List<EggMovementRow>>();
        Assert.NotNull(assignedRows);
        Assert.Contains(assignedRows, row => row.Id == movementAId);

        var unassigned = await fix.Worker.GetAsync(
            $"/api/v1/stock/lots/{lotBId}/movements");
        Assert.Equal(HttpStatusCode.NotFound, unassigned.StatusCode);
    }

    // #388/#612 — each raw-SQL contract is explicit. Sales FIFO stays farm-wide
    // for a restricted Worker (current SalesFlow behavior); the two AdminOnly
    // reconciliation locks retain the explicit flock predicate because they
    // bypass global filters. One assertion family per SQL statement keeps each
    // mutation causal.
    [Fact]
    public async Task EggLotRawSqlPaths_HonorTheirDistinctSalesAndAdminContracts()
    {
        var fix = await SeedAsync();
        var lotAId = Guid.NewGuid();
        var lotBId = Guid.NewGuid();
        Guid dailyAId = default;
        Guid dailyBId = default;

        await factory.WithTenantScopeAsync(fix.AccountId, async db =>
        {
            dailyAId = await db.DailyEntries.Where(e => e.FlockId == fix.FlockA)
                .Select(e => e.Id).SingleAsync();
            dailyBId = await db.DailyEntries.Where(e => e.FlockId == fix.FlockB)
                .Select(e => e.Id).SingleAsync();

            db.EggLots.Add(EggLot.Create(
                lotAId, fix.AccountId, fix.FlockA, Today, fix.GradeId, 25,
                dailyEntryId: dailyAId));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), fix.AccountId, lotAId, EggMovementType.Production,
                25, "DailyEntry", dailyAId, DateTimeOffset.UtcNow));
            db.EggLots.Add(EggLot.Create(
                lotBId, fix.AccountId, fix.FlockB, Today, fix.GradeId, 30,
                dailyEntryId: dailyBId));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), fix.AccountId, lotBId, EggMovementType.Production,
                30, "DailyEntry", dailyBId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        var scope = new FlockScope();
        scope.Resolve(false, [fix.FlockA]);
        var tenant = new TenantContext();
        tenant.Resolve(fix.AccountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        await using var restrictedDb = new AppDbContext(options, tenant, scope);
        var repo = new EggLotRepository(restrictedDb);

        // SalesFlow contract: FIFO allocation remains farm-wide (#612).
        var fifo = await repo.GetAvailableFifoLockedAsync(
            fix.AccountId, [fix.GradeId], Today, CancellationToken.None);
        Assert.Contains(fifo, l => l.Id == lotAId);
        Assert.Contains(fifo, l => l.Id == lotBId);

        // Admin reconciliation by explicit ids remains flock-scoped.
        var byIds = await repo.GetByIdsLockedAsync(
            fix.AccountId, [lotAId, lotBId], CancellationToken.None);
        Assert.Contains(byIds, l => l.Id == lotAId);
        Assert.DoesNotContain(byIds, l => l.Id == lotBId);

        // Admin reconciliation by DailyEntry remains flock-scoped.
        var byAssignedEntry = await repo.GetByDailyEntryLockedAsync(
            fix.AccountId, dailyAId, CancellationToken.None);
        Assert.Contains(byAssignedEntry, l => l.Id == lotAId);
        var byUnassignedEntry = await repo.GetByDailyEntryLockedAsync(
            fix.AccountId, dailyBId, CancellationToken.None);
        Assert.Empty(byUnassignedEntry);
    }

    // #388 — the two scoped-write bypass lookups (GetByIdForFlockScopedWriteAsync,
    // FindByNaturalKeyForFlockScopedWriteAsync) use IgnoreQueryFilters() to see a
    // live, not request-scope-snapshotted, flock/entry — but that bypass also
    // strips the tenant filter. Each repository reinstates AccountId explicitly;
    // this fact proves that reinstatement, independent of the AppDbContext combined
    // filters (which a fresh Unrestricted FlockScope here does not exercise).
    [Fact]
    public async Task ScopedWriteBypasses_ReinstateTenantIsolation()
    {
        var ownerEmailA = $"bypass-a-{Guid.NewGuid():N}@test.local";
        var accountA = await factory.SeedAccountWithUserAsync(ownerEmailA);
        var farmA = Guid.NewGuid();
        var houseA = Guid.NewGuid();
        var flockA = await factory.SeedFlockAsync(accountA, farmA, houseA);
        var entryAId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Add(DailyEntry.Create(entryAId, accountA, farmA, houseA, flockA, Today));
            await db.SaveChangesAsync();
        });

        var ownerEmailB = $"bypass-b-{Guid.NewGuid():N}@test.local";
        var accountB = await factory.SeedAccountWithUserAsync(ownerEmailB);
        var farmB = Guid.NewGuid();
        var houseB = Guid.NewGuid();
        var flockB = await factory.SeedFlockAsync(accountB, farmB, houseB);
        var entryBId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountB, async db =>
        {
            db.DailyEntries.Add(DailyEntry.Create(entryBId, accountB, farmB, houseB, flockB, Today));
            await db.SaveChangesAsync();
        });

        var tenant = new TenantContext();
        tenant.Resolve(accountA);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        await using var db = new AppDbContext(options, tenant, new FlockScope());
        var flocks = new FlockRepository(db);
        var entries = new DailyEntryRepository(db);

        // Positive controls: account A's own rows are still reachable through the bypass.
        var ownFlock = await flocks.GetByIdForFlockScopedWriteAsync(flockA, accountA);
        Assert.NotNull(ownFlock);
        Assert.Equal(flockA, ownFlock!.Id);

        var ownEntry = await entries.FindByNaturalKeyForFlockScopedWriteAsync(accountA, farmA, houseA, flockA, Today);
        Assert.NotNull(ownEntry);
        Assert.Equal(entryAId, ownEntry!.Id);

        // Negative controls: account B's rows must NOT be reachable under accountA's tenant.
        var foreignFlock = await flocks.GetByIdForFlockScopedWriteAsync(flockB, accountA);
        Assert.Null(foreignFlock);

        var foreignEntry = await entries.FindByNaturalKeyForFlockScopedWriteAsync(accountA, farmB, houseB, flockB, Today);
        Assert.Null(foreignEntry);
    }

    // #388 — Codex FIX-NOW follow-up: Submit's stale request-scope/live-
    // assignment fix (SubmitDailyEntryTests.
    // Submit_AssignmentAddedAfterScopeSnapshot_StillChecksArchivedFlockLifecycle)
    // covered only Submit. This fact represents the same race — a request-start
    // FlockScope snapshot pinned to A, then a SEPARATE transaction adds live
    // assignment B — against the three sibling write handlers: RecordDailyEntry,
    // RecordFeedUsage and RecordWaterUsage, including their natural-key and
    // provenance reads.
    [Fact]
    public async Task ScopedWrites_AssignmentAddedAfterScopeSnapshot_UseLiveFlockAndNaturalKeyState()
    {
        var ownerEmail = $"owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var gradeId = grades["Large"];
        var houseA = Guid.NewGuid();
        var houseB = Guid.NewGuid();
        var flockA = await factory.SeedFlockAsync(accountId, farmId, houseA);
        var flockB = await factory.SeedFlockAsync(accountId, farmId, houseB);
        var entryDate = Today;

        // Flock B's daily entry, recorded under its OWN houseB — feed/water
        // provenance below joins on (farm, house, flock, date), so this must
        // be the same house the entry actually used (unlike SeedAsync's
        // random per-call house).
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail));
        var recordResponse = await owner.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = houseB, flockId = flockB, date = entryDate,
            totalEggs = 600, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0, grades = new[] { new { eggGradeId = gradeId, quantity = 600 } }
        });
        Assert.Equal(HttpStatusCode.Created, recordResponse.StatusCode);
        var entryBId = (await recordResponse.Content.ReadFromJsonAsync<RecordedDto>())!.Id;

        var workerEmail = $"worker-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, role: null);
        var workerId = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.AccountId == accountId && u.Email == workerEmail)
                .Select(u => u.Id).SingleAsync());

        // Worker assignment A exists BEFORE the request scope below pins its
        // FlockScope snapshot.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                Guid.NewGuid(), accountId, workerId, farmId: null, houseId: null, flockId: flockA));
            await db.SaveChangesAsync();
        });

        var requestScope = factory.Services.CreateScope();
        requestScope.ResolveTenantAndActor(accountId, workerId, workerEmail, roles: []);
        requestScope.ServiceProvider.GetRequiredService<FlockScope>()
            .Resolve(false, [flockA]);

        // Separate DB scope: live assignment B, plus a feed item/lot with
        // enough stock for the feed handler below.
        var feedItemId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                Guid.NewGuid(), accountId, workerId, farmId: null, houseId: null, flockId: flockB));
            db.InventoryItems.Add(InventoryItem.Create(
                feedItemId, accountId, farmId, "Sibling-race feed",
                InventoryCategory.Feed, "kg", Money.Zero("USD")));
            db.InventoryLots.Add(InventoryLot.Create(
                Guid.NewGuid(), accountId, feedItemId, entryDate,
                quantity: 100m, Money.Zero("USD"), lotNumber: null, expiryDate: null));
            await db.SaveChangesAsync();
        });

        var dailyHandler = requestScope.ServiceProvider.GetRequiredService<RecordDailyEntryHandler>();
        var feedHandler = requestScope.ServiceProvider.GetRequiredService<RecordFeedUsageHandler>();
        var waterHandler = requestScope.ServiceProvider.GetRequiredService<RecordWaterUsageHandler>();

        // While B is live: all three handlers, driven from the pinned-to-A
        // request scope, must reach flock B and its natural-key state, not
        // NotFound/duplicate from the stale snapshot.
        var dailyResult = await dailyHandler.HandleAsync(
            new RecordDailyEntryCommand(
                farmId, houseB, flockB, entryDate,
                600, 0, 0, 0, 0,
                [new GradeQuantityDto(gradeId, 600)]),
            accountId, CancellationToken.None);
        Assert.True(dailyResult.IsSuccess);
        Assert.Equal(entryBId, dailyResult.Value);

        var feedResult = await feedHandler.HandleAsync(
            new RecordFeedUsageCommand(flockB, feedItemId, entryDate, 5m, Note: null),
            accountId, CancellationToken.None);
        Assert.True(feedResult.IsSuccess);

        var waterResult = await waterHandler.HandleAsync(
            new RecordWaterUsageCommand(flockB, entryDate, 10m, "L", "Municipal", null, null, null),
            accountId, CancellationToken.None);
        Assert.True(waterResult.IsSuccess);

        var feedUsageId = feedResult.Value.FeedUsageId;
        var waterUsageId = waterResult.Value;

        requestScope.Dispose();

        // A fresh, unrestricted context — never the stale-pinned handler
        // context — proves the reused entry id and the persisted provenance
        // links, and captures the baseline this test proves stays unchanged
        // after the rejected calls below.
        var lotQuantityBaseline = 0m;
        var feedUsageCountBaseline = 0;
        var waterUsageCountBaseline = 0;
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var reusedEntry = await db.DailyEntries.SingleAsync(e => e.Id == entryBId);
            Assert.Equal(flockB, reusedEntry.FlockId);

            var feedUsage = await db.FeedUsages.SingleAsync(f => f.Id == feedUsageId);
            Assert.Equal(entryBId, feedUsage.DailyEntryId);

            var waterUsage = await db.WaterUsages.SingleAsync(w => w.Id == waterUsageId);
            Assert.Equal(entryBId, waterUsage.DailyEntryId);

            lotQuantityBaseline = await db.InventoryLots
                .Where(l => l.InventoryItemId == feedItemId)
                .SumAsync(l => l.QuantityAvailable);
            feedUsageCountBaseline = await db.FeedUsages.CountAsync(f => f.FlockId == flockB);
            waterUsageCountBaseline = await db.WaterUsages.CountAsync(w => w.FlockId == flockB);
        });

        // Archive flock B in a separate scope.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = await db.Flocks.SingleAsync(f => f.Id == flockB);
            Assert.True(flock.Archive(entryDate).IsSuccess);
            await db.SaveChangesAsync();
        });

        // A NEW request scope pinned to A, while assignment B is still live.
        // The live FlockScopeGuard check passes (it reads assignments fresh),
        // but the scoped-write flock lookup must see B's CURRENT (archived)
        // state rather than the pinned-to-A snapshot, and reject with the
        // exact lifecycle code.
        var secondRequestScope = factory.Services.CreateScope();
        secondRequestScope.ResolveTenantAndActor(accountId, workerId, workerEmail, roles: []);
        secondRequestScope.ServiceProvider.GetRequiredService<FlockScope>()
            .Resolve(false, [flockA]);

        var secondDailyHandler = secondRequestScope.ServiceProvider.GetRequiredService<RecordDailyEntryHandler>();
        var secondFeedHandler = secondRequestScope.ServiceProvider.GetRequiredService<RecordFeedUsageHandler>();
        var secondWaterHandler = secondRequestScope.ServiceProvider.GetRequiredService<RecordWaterUsageHandler>();

        var rejectedDaily = await secondDailyHandler.HandleAsync(
            new RecordDailyEntryCommand(
                farmId, houseB, flockB, entryDate,
                600, 0, 0, 0, 0,
                [new GradeQuantityDto(gradeId, 600)]),
            accountId, CancellationToken.None);
        Assert.True(rejectedDaily.IsFailure);
        Assert.Equal("DailyEntry.FlockNotActive", rejectedDaily.Error.Code);

        var rejectedFeed = await secondFeedHandler.HandleAsync(
            new RecordFeedUsageCommand(flockB, feedItemId, entryDate, 5m, Note: null),
            accountId, CancellationToken.None);
        Assert.True(rejectedFeed.IsFailure);
        Assert.Equal("FeedUsage.FlockNotActive", rejectedFeed.Error.Code);

        var rejectedWater = await secondWaterHandler.HandleAsync(
            new RecordWaterUsageCommand(flockB, entryDate, 10m, "L", "Municipal", null, null, null),
            accountId, CancellationToken.None);
        Assert.True(rejectedWater.IsFailure);
        Assert.Equal("WaterUsage.FlockNotActive", rejectedWater.Error.Code);

        secondRequestScope.Dispose();

        // Another fresh unrestricted context: the rejected calls left no
        // additional usage/water rows and consumed no further lot stock.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lotQuantityAfter = await db.InventoryLots
                .Where(l => l.InventoryItemId == feedItemId)
                .SumAsync(l => l.QuantityAvailable);
            Assert.Equal(lotQuantityBaseline, lotQuantityAfter);

            Assert.Equal(feedUsageCountBaseline, await db.FeedUsages.CountAsync(f => f.FlockId == flockB));
            Assert.Equal(waterUsageCountBaseline, await db.WaterUsages.CountAsync(w => w.FlockId == flockB));
        });
    }

    // #512 — the flock-scope half of INV-1 has to survive the NEW read
    // predicates (`search`, `eligibility`) rather than being applied to the
    // legacy list only. #613 makes the combined `AccountId AND flock-scope`
    // filter a structural invariant of the model, so a discovery query that
    // reaches Flocks through anything other than the filtered DbSet would keep
    // this suite green by accident; the direct-EF twin below closes that hole.
    //
    // Flocks A (Active) and B (Depleted) already exist; add one Archived B flock
    // so `eligibility=all` has a B row to leak. Every query shape is then run as
    // both the scoped Worker and the Owner: the Worker must never see a B row and
    // must never see nothing (a substitute result would look like filtering),
    // while the Owner control proves the same query CAN reach B — so an empty
    // Worker answer is the filter working, not a fixture that failed to seed.
    [Fact]
    public async Task ScopedWorker_FlockDiscovery_NeverReachesUnassignedFlock()
    {
        var fix = await SeedAsync();
        var archivedB = await RenameDiscoveryFlocksAsync(fix);

        // Six shapes whose result set CONTAINS the assigned flock, so the Worker's
        // answer must be non-empty AND all-A: an empty list would otherwise let a
        // query that filtered everything away — or a fixture that failed to seed —
        // read as scope enforcement.
        var shapes = new[]
        {
            "?search=zzz&limit=500",                            // literal search, default policy
            "?search=zzz&eligibility=all&limit=500",            // literal search + widest policy
            "?search=zzz&includeArchived=true&limit=500",       // legacy alias + search
            "?search=zzz&eligibility=active&limit=500",         // narrowest policy
            "?eligibility=all&limit=500&offset=0",              // policy only
            "?limit=500&offset=0",                              // legacy list, unchanged
        };
        // Deliberately separate: windows narrowed past A's single row, where an
        // empty Worker answer is correct and only "never B" is honest.
        var pastA = new[] { "?limit=1&offset=1", "?limit=2&offset=5" };

        foreach (var query in shapes)
        {
            var asWorker = await fix.Worker.GetAsync("/api/v1/flocks" + query);
            Assert.Equal(HttpStatusCode.OK, asWorker.StatusCode);
            var workerRows = await asWorker.Content.ReadFromJsonAsync<List<FlockDiscoveryRow>>();
            Assert.NotNull(workerRows);
            Assert.NotEmpty(workerRows);
            Assert.All(workerRows, f => Assert.Equal(fix.FlockA, f.Id));

            var asOwner = await fix.Owner.GetAsync("/api/v1/flocks" + query);
            Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
            var ownerRows = await asOwner.Content.ReadFromJsonAsync<List<FlockDiscoveryRow>>();
            Assert.NotNull(ownerRows);
            Assert.Contains(ownerRows, f => f.Id == fix.FlockB || f.Id == archivedB);
        }

        foreach (var query in pastA)
        {
            var asWorker = await fix.Worker.GetAsync("/api/v1/flocks" + query);
            Assert.Equal(HttpStatusCode.OK, asWorker.StatusCode);
            var workerRows = await asWorker.Content.ReadFromJsonAsync<List<FlockDiscoveryRow>>();
            Assert.NotNull(workerRows);
            Assert.All(workerRows, f => Assert.Equal(fix.FlockA, f.Id));

            var asOwner = await fix.Owner.GetAsync("/api/v1/flocks" + query);
            Assert.NotNull(asOwner);
            var ownerRows = await asOwner.Content
                .ReadFromJsonAsync<List<FlockDiscoveryRow>>();
            Assert.NotNull(ownerRows);
            // The unscoped account-wide walk at that offset is non-empty — the account
            // has more than one flock — so an empty Worker page is the filter and not
            // the end of the data. Deliberately no B-contains here: the ordering IS
            // already Name,Id, but this suite's un-renamed flocks are named
            // `Flock-<guid>` by the shared harness (#512 renames only A and B, to
            // "Zzz …"), so which two of the remaining GUID-named flocks a 1-or-2-row
            // unscoped window returns carries no meaning to assert. Non-emptiness is
            // the part worth pinning.
        }

        // A search naming the UNASSIGNED flocks specifically is EMPTY for the
        // Worker — never silently satisfied by the assigned neighbour. This is
        // the one place an empty answer is the assertion, and it is why A does
        // not carry these two exact names.
        foreach (var name in new[] { "zzz%20bravo%20alpha%20shared", "zzz%20bravo%20archived" })
        {
            var rows = await (await fix.Worker.GetAsync(
                $"/api/v1/flocks?search={name}&eligibility=all&limit=500"))
                .Content.ReadFromJsonAsync<List<FlockDiscoveryRow>>();
            Assert.NotNull(rows);
            Assert.Empty(rows);
        }

        // The premise of that loop: A carries the broad `zzz` marker — which is
        // what makes the six shapes above non-empty — but NOT either of the two
        // exact names, which is what makes an empty answer honest here. Both
        // halves are load-bearing, so both are asserted.
        var asWorkerWide = await fix.Worker.GetAsync(
            "/api/v1/flocks?search=zzz&eligibility=all&limit=500");
        var wideRows = await asWorkerWide.Content
            .ReadFromJsonAsync<List<FlockDiscoveryRow>>();
        Assert.NotNull(wideRows);
        Assert.Single(wideRows);
        Assert.Equal(fix.FlockA, wideRows[0].Id);
        Assert.Equal("Zzz Alpha Assigned", wideRows[0].Name);
    }

    // Shared by the HTTP test and the direct-EF twin below: the two run against
    // the same fixture data, so they must apply the SAME mutation or one of them
    // asserts against rows the other already renamed. Returns the Archived B id.
    //
    // The names are the test's own data, not the harness's: SeedFlockAsync names
    // a flock `Flock-xxxxxxxx`, which no search shape here could match, so the
    // whole suite would silently degenerate into "nothing matches, as expected".
    //  * A (assigned, Active) = `Zzz Alpha Assigned` — the bare `zzz` marker with
    //    no `bravo`, so every `search=zzz` shape has an honest NON-EMPTY Worker
    //    answer, while A matches neither of the two exact B names below.
    //  * B (unassigned, Active) = `Zzz Bravo Alpha Shared` — matched by the broad
    //    `zzz` and by `zzz alpha` (both words appear), but by NEITHER of the two
    //    exact names. Its Active status matters: an unassigned row that
    //    eligibility removes anyway would make the scope assertion vacuous.
    //  * Archived B = `Zzz Bravo Archived`, so `eligibility=all` has an
    //    unassigned Archived row to leak.
    private async Task<Guid> RenameDiscoveryFlocksAsync(Fixture fix)
    {
        var archivedB = Guid.NewGuid();
        await factory.WithTenantScopeAsync(fix.AccountId, async db =>
        {
            var a = await db.Flocks.FirstAsync(f => f.Id == fix.FlockA);
            Assert.True(a.Update("Zzz Alpha Assigned", a.Breed, a.PlacementDate, a.InitialCount).IsSuccess);
            var b = await db.Flocks.FirstAsync(f => f.Id == fix.FlockB);
            // "Alpha" is in B's name on purpose. A bare `search=zzz alpha` would
            // reach A (`Zzz Alpha Assigned`) and NOT this row — B's second word
            // is Bravo — so the scoped "only A" assertion would pass with the
            // flock filter deleted and the test would prove nothing. Every shape
            // here therefore uses the broad `zzz`, which DOES reach B.
            Assert.True(b.Update("Zzz Bravo Alpha Shared", b.Breed, b.PlacementDate, b.InitialCount).IsSuccess);
            // B is seeded Active; say so, because the EF twin's control depends
            // on it surviving the Active-or-Depleted eligibility predicate.
            Assert.Equal(FlockStatus.Active, b.Status);
            var ab = Cluckwork.Domain.Flocks.Flock.Create(
                archivedB, fix.AccountId, fix.FarmId, Guid.NewGuid(),
                "Zzz Bravo Archived", "Test Breed", new DateOnly(2026, 1, 1), 50);
            ab.Deplete(new DateOnly(2026, 2, 1));
            ab.Archive(new DateOnly(2026, 3, 1));
            db.Flocks.Add(ab);
            await db.SaveChangesAsync();
        });
        return archivedB;
    }

    // The repository-layer twin: the same predicate set under a hand-built
    // Worker scope, straight against the filtered DbSet. If a future discovery
    // query bypasses the model filter, the HTTP assertion above can still pass
    // behind an upstream guard — this one cannot.
    [Fact]
    public async Task FlockDiscoveryPredicate_UnderWorkerScope_ExcludesUnassignedFlock()
    {
        var fix = await SeedAsync();
        var archivedB = await RenameDiscoveryFlocksAsync(fix);

        // Eligibility → literal search → Name, Id → window, with the escape
        // argument, exactly as the repository builds it (#512 T010). The search
        // is the broad `zzz`, which BOTH A and B carry, so the predicate is
        // satisfied either way and only the scope clause can decide the answer —
        // a search that merely matched nothing would make the scoped assertion
        // below vacuous, which is exactly the wrong-guard failure this file's
        // twin was reviewed for.
        static IQueryable<Flock> Discovery(IQueryable<Flock> q) => q
            .Where(f => f.Status == FlockStatus.Active || f.Status == FlockStatus.Depleted)
            .Where(f => EF.Functions.ILike(f.Name, "%zzz%", "\\"))
            .OrderBy(f => f.Name).ThenBy(f => f.Id);

        var tenant = new TenantContext();
        tenant.Resolve(fix.AccountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;

        // Control: the SAME query with ONLY the flock half of the filter
        // bypassed reaches both. `IgnoreQueryFilters` drops the AccountId clause
        // too — the whole collection shares this database — so the tenant clause
        // is re-imposed explicitly, and this stays a flock-filter-only delta.
        // That is what makes the scoped assertion below causal: it proves both
        // rows are findable by this predicate, in this tenant, once the scope
        // clause is gone.
        var unscopedScope = new FlockScope();
        unscopedScope.Resolve(true, []);
        await using (var unscoped = new AppDbContext(options, tenant, unscopedScope))
        {
            var reachable = await Discovery(
                    unscoped.Flocks.IgnoreQueryFilters().AsNoTracking()
                        .Where(f => f.AccountId == fix.AccountId))
                .Skip(0).Take(50)
                .ToListAsync();
            Assert.Contains(reachable, f => f.Id == fix.FlockA);
            Assert.Contains(reachable, f => f.Id == fix.FlockB);
        }

        // Scoped to flock A: non-empty, and ONLY flock A.
        var scope = new FlockScope();
        scope.Resolve(false, [fix.FlockA]);
        await using (var db = new AppDbContext(options, tenant, scope))
        {
            var eligible = await Discovery(db.Flocks.AsNoTracking())
                .Skip(0).Take(50).ToListAsync();
            Assert.NotEmpty(eligible);
            Assert.All(eligible, f => Assert.Equal(fix.FlockA, f.Id));
            Assert.DoesNotContain(eligible, f => f.Id == fix.FlockB);

            // And the same holds with the Archived rows admitted: the Archived
            // B flock is unassigned too, so `eligibility=all` must still answer
            // with A alone.
            var wide = await db.Flocks.AsNoTracking()
                .Where(f => EF.Functions.ILike(f.Name, "%zzz%", "\\"))
                .OrderBy(f => f.Name).ThenBy(f => f.Id)
                .Skip(0).Take(50)
                .ToListAsync();
            Assert.NotEmpty(wide);
            Assert.All(wide, f => Assert.Equal(fix.FlockA, f.Id));
            Assert.DoesNotContain(wide, f => f.Id == archivedB);
        }
    }

    private sealed record FlockDiscoveryRow(Guid Id, string Name, string Status);

    private sealed record RecordedDto(Guid Id);

    private sealed record Fixture(
        Guid AccountId, Guid FarmId, Guid FlockA, Guid FlockB, Guid GradeId,
        Guid EntryAId, Guid EntryBId,
        HttpClient Worker, HttpClient Owner, HttpClient Manager, Guid WorkerId);

    private sealed record FlockRow(Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed);
    private sealed record DailyEntryRow(Guid Id, Guid FarmId, Guid HouseId, Guid FlockId, DateOnly Date, string Status);
    private sealed record WaterUsageRow(Guid Id, Guid FlockId, DateOnly Date, decimal Quantity, string Unit, string Source);
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);
    private sealed record EggMovementRow(
        Guid Id, string MovementType, int QuantityDelta,
        string ReferenceType, Guid ReferenceId, string? Reason,
        DateTimeOffset CreatedAtUtc);

    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    private static async Task<string> StepUpAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
    }

    private static async Task<HttpResponseMessage> AssignFlockAsync(
        HttpClient client, Guid userId, Guid flockId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/users/{userId}/flock-assignments")
        {
            Content = JsonContent.Create(new { flockId }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(client));
        return await client.SendAsync(request);
    }
}
