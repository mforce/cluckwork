namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

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
        Assert.Equal(HttpStatusCode.Created, (await owner.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId = flockA, date = Today,
            totalEggs = 50, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0, grades = new[] { new { eggGradeId = gradeId, quantity = 50 } }
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId = flockB, date = Today,
            totalEggs = 60, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0, grades = new[] { new { eggGradeId = gradeId, quantity = 60 } }
        })).StatusCode);
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

        return new Fixture(accountId, farmId, flockA, flockB, gradeId, worker, owner, manager, workerId);
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

    // The raw-SQL FOR UPDATE paths bypass the query filter; the explicit
    // predicate must scope them the same way. All three callers are AdminOnly
    // paths, so drive the repository with a HAND-BUILT restricted context —
    // the same mechanism as ExpenseFilter_FarmWideVisible_UnassignedExcluded.
    [Fact]
    public async Task EggLotRawSqlPath_IsScoped()
    {
        var fix = await SeedAsync();

        // Seed one egg lot on flock A and one on flock B (owner scope) with
        // the REAL flock ids — EggLot.Create(id, accountId, flockId, date,
        // gradeId, qty), plus the Production EggInventoryMovement row that
        // keeps the #101 ledger invariant, mirroring TestHarness.SeedEggLotAsync.
        var lotAId = Guid.NewGuid();
        var lotBId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(fix.AccountId, async db =>
        {
            db.EggLots.Add(EggLot.Create(lotAId, fix.AccountId, fix.FlockA, Today, fix.GradeId, 25));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), fix.AccountId, lotAId, EggMovementType.Production,
                25, "DailyEntry", Guid.NewGuid(), DateTimeOffset.UtcNow));
            db.EggLots.Add(EggLot.Create(lotBId, fix.AccountId, fix.FlockB, Today, fix.GradeId, 30));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), fix.AccountId, lotBId, EggMovementType.Production,
                30, "DailyEntry", Guid.NewGuid(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        // Restricted context: scope driven to flock A only.
        var scope = new FlockScope();
        scope.Resolve(false, [fix.FlockA]);
        var tenant = new TenantContext();
        tenant.Resolve(fix.AccountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        await using var restrictedDb = new AppDbContext(options, tenant, scope);

        var repo = new EggLotRepository(restrictedDb);
        var lots = await repo.GetByIdsLockedAsync(fix.AccountId, [lotAId, lotBId], CancellationToken.None);

        Assert.Contains(lots, l => l.Id == lotAId);
        Assert.DoesNotContain(lots, l => l.Id == lotBId);
    }

    private sealed record Fixture(
        Guid AccountId, Guid FarmId, Guid FlockA, Guid FlockB, Guid GradeId,
        HttpClient Worker, HttpClient Owner, HttpClient Manager, Guid WorkerId);

    private sealed record FlockRow(Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed);
    private sealed record DailyEntryRow(Guid Id, Guid FarmId, Guid HouseId, Guid FlockId, DateOnly Date, string Status);
    private sealed record WaterUsageRow(Guid Id, Guid FlockId, DateOnly Date, decimal Quantity, string Unit, string Source);
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);

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
