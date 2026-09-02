namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Expenses;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

// #512 US4 — a returned ROW names its own flock / customer, so a picker consumer
// can render a list without holding a catalogue of every referenced entity. The
// fields are additive: `flockName`, `flockStatus`, `customerName` join existing
// envelopes and nothing existing changes meaning.
//
// What these tests are actually for is not "the string is present" — that a name
// appears is cheap. Three properties are, and everything below serves one of them:
//
//   1. NAMES ARE CURRENT, NOT SNAPSHOTTED. A row names its flock as the flock is
//      named NOW, including when that flock is Archived or Depleted. A picker may
//      refuse to offer an Archived flock for NEW selection (US1 eligibility), but a
//      2026 feed record must still say which flock it belongs to. Any "resolve it
//      like the picker does" shortcut that filters by eligibility loses exactly
//      those rows, so each contract seeds Archived AND Depleted references.
//   2. REFERENCES ARE NOT PICKER RESULTS. A reference outside the first discovery
//      page still resolves (the row projection is a keyed bulk read by id, not a
//      lookup into whatever page of `/flocks` the client happened to fetch).
//   3. SCOPE IS ENFORCED BY THE QUERY, NOT BY THE RESPONSE SHAPE. A row must never
//      name another tenant's or another worker's entity, and must never lose its
//      own name because a foreign id entered the same read.
//
// Plus the shape guards (ShapeProbe): the read is ONE grouped read per page, the
// assignment projection is ONE left join, and the flock-list movement aggregate is
// bounded to the returned ids. A wrong shape returns right data, so those are the
// only properties that cannot be guarded by asserting on a response.
public class NamedRowProjectionTests : IClassFixture<NamedRowProjectionFactory>, IAsyncLifetime
{
    private const string RouteEntries = "daily-entries";
    private const string RouteFeed = "inventory/usage";
    private const string RouteWater = "water-usage";
    private const string RouteExpenses = "expenses";
    private const string RouteSales = "sales";

    // A calendar window far from "today" and far from the migrations' base rows.
    // Date filters are open-ended in both directions, so nothing else in a fresh
    // database can land inside it.
    private static readonly DateOnly From = new(2026, 4, 10);

    private static readonly string Active = nameof(FlockStatus.Active);
    private static readonly string Depleted = nameof(FlockStatus.Depleted);
    private static readonly string Archived = nameof(FlockStatus.Archived);

    private readonly NamedRowProjectionFactory factory;

    public NamedRowProjectionTests(NamedRowProjectionFactory factory) => this.factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;
    // Closing the window is DisposeAsync's job because a test that asserts BEFORE
    // the request (or fails mid-way) never reaches its window's Dispose, and a
    // leaked window would keep recording into the next test's counts.
    public Task DisposeAsync()
    {
        factory.Probe.Disarm();
        return Task.CompletedTask;
    }

    // The probe instance belonging to THIS suite's factory — the one requests here
    // actually traverse. Never a process-wide "current" probe: see ShapeProbe.
    private ShapeProbe Probe => factory.Probe;

    // One farm's worth of named references: an Active, Archived and Depleted flock
    // (the two latter statuses are exactly what a picker-eligibility filter would
    // hide, so each contract needs them), nullable-flock and farm-wide shapes, two
    // customers, and 60 filler flocks plus 60 filler customers. The fillers exist so
    // a reference cannot be ASSUMED to sit inside a discovery page — the page-50 test
    // measures the ranks rather than trusting them.
    //
    // The ids are per-test GUIDs and the names carry a per-test marker, so tests do
    // not read each other's rows; the factory's database is shared across the class.
    private sealed class Fixture
    {
        // The seeded tenant. Entities are built INSIDE this tenant's scope with this
        // id, because the stamp interceptor accepts an empty id (and stamps the
        // resolved tenant) but rejects a non-empty one that differs — so passing a
        // shared constant here would throw for every tenant but the default.
        public required Guid AccountId { get; init; }
        public required Guid FarmId { get; init; }
        public required Guid HouseId { get; init; }

        public required string Marker { get; init; }
        public required Guid ActiveFlock { get; init; }
        public required Guid DepletedFlock { get; init; }
        public required Guid ArchivedFlock { get; init; }
        public required Guid MovementFlock { get; init; }
        public required Guid CustomerA { get; init; }
        public required Guid CustomerC { get; init; }
        public required Guid ItemId { get; init; }
        public required Guid CategoryId { get; init; }
        public required Guid WorkerId { get; init; }
        public required Guid EntryActive { get; init; }
        public required Guid EntryArchived { get; init; }
        public required Guid EntryDepleted { get; init; }
        public required Guid WaterActive { get; init; }
        public required Guid WaterArchived { get; init; }
        public required Guid FeedActive { get; init; }
        public required Guid FeedArchived { get; init; }
        public required Guid OrderA { get; init; }
        public required Guid OrderC { get; init; }
        public required Guid ExpenseActive { get; init; }
        public required Guid ExpenseArchived { get; init; }
        public required Guid ExpenseNone { get; init; }
        public required Guid FarmWideAssignment { get; init; }
        public required Guid FlockAssignment { get; init; }

        public string ActiveName => $"{Marker} pf active";
        public string DepletedName => $"{Marker} pf depleted";
        public string ArchivedName => $"{Marker} pf archived";
        public string MovementName => $"{Marker} pf movement";
        public string CustomerAName => $"{Marker} customer aa";
        public string CustomerCName => $"{Marker} customer cc";
        public string FillerName(int i) => $"{Marker} filler {i:D2}";

        public Guid[] OwnedEntryIds() => [EntryActive, EntryArchived, EntryDepleted];
        public Guid[] OwnedOrderIds() => [OrderA, OrderC];
    }

    private static string Q(string value) => Uri.EscapeDataString(value);

    // A GET that must succeed, with the response body reported when it does not.
    // EnsureSuccessStatusCode reports only the status code, and a 400 from a
    // query-string binding failure is otherwise invisible.
    // Same contract as GetRows for a caller that supplies its own query string.
    private Task<IReadOnlyDictionary<Guid, FlockReference>> FlockNamesAsync(
        Guid accountId, IReadOnlyCollection<Guid> ids) =>
        factory.WithTenantScopeAsync(accountId, async db =>
            await new Cluckwork.Infrastructure.Repositories.FlockRepository(db)
                .GetDisplayNamesAsync(ids));

    private Task<IReadOnlyDictionary<Guid, CustomerReference>> CustomerNamesAsync(
        Guid accountId, IReadOnlyCollection<Guid> ids) =>
        factory.WithTenantScopeAsync(accountId, async db =>
            await new Cluckwork.Infrastructure.Repositories.CustomerRepository(db)
                .GetDisplayNamesAsync(ids));

    private static async Task<List<T>> GetPagedAsync<T>(HttpClient client, string routeWithQuery)
    {
        var response = await client.GetAsync($"/api/v1/{routeWithQuery}");
        Assert.True(response.IsSuccessStatusCode,
            $"GET {routeWithQuery} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<List<T>>() ?? [];
    }

    private static async Task<List<T>> GetRows<T>(HttpClient client, string route)
    {
        var response = await client.GetAsync($"/api/v1/{route}?limit=500");
        Assert.True(response.IsSuccessStatusCode,
            $"GET {route} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<List<T>>() ?? [];
    }

    // Seeds one farm's reference graph directly through EF (tenant scope resolved,
    // so the stamp interceptor and filters behave as in a request). Direct writes
    // rather than HTTP for the bulk fillers: 120 filler entities over the API would
    // make the fixture the slowest thing in the suite, and none of these guards is
    // about the write path.
    private async Task<(HttpClient Client, Fixture F)> SeedAsync()
    {
        var email = $"pj-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var f = await SeedGraphAsync(accountId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, f);
    }

    // The same graph, addressed as a different account — the tenant-isolation
    // control. Nothing about the shape differs; only the tenant does.
    private async Task<Fixture> SeedGraphAsync(Guid accountId, string? marker = null)
    {
        var marker2 = marker ?? $"pj{Guid.NewGuid():N}";
        var f = new Fixture
        {
            AccountId = accountId,
            FarmId = SeedDefaults.FarmId,
            HouseId = SeedDefaults.HouseId,
            Marker = marker2,
            ActiveFlock = Guid.NewGuid(),
            DepletedFlock = Guid.NewGuid(),
            ArchivedFlock = Guid.NewGuid(),
            MovementFlock = Guid.NewGuid(),
            CustomerA = Guid.NewGuid(),
            CustomerC = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            WorkerId = Guid.NewGuid(),
            EntryActive = Guid.NewGuid(),
            EntryArchived = Guid.NewGuid(),
            EntryDepleted = Guid.NewGuid(),
            WaterActive = Guid.NewGuid(),
            WaterArchived = Guid.NewGuid(),
            FeedActive = Guid.NewGuid(),
            FeedArchived = Guid.NewGuid(),
            OrderA = Guid.NewGuid(),
            OrderC = Guid.NewGuid(),
            ExpenseActive = Guid.NewGuid(),
            ExpenseArchived = Guid.NewGuid(),
            ExpenseNone = Guid.NewGuid(),
            FarmWideAssignment = Guid.NewGuid(),
            FlockAssignment = Guid.NewGuid(),
        };

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Flocks.Add(Build(f.ActiveFlock, accountId, f.ActiveName, FlockStatus.Active));
            db.Flocks.Add(Build(f.ArchivedFlock, accountId, f.ArchivedName, FlockStatus.Archived));
            db.Flocks.Add(Build(f.MovementFlock, accountId, f.MovementName, FlockStatus.Active));
            var depleted = Build(f.DepletedFlock, accountId, f.DepletedName, FlockStatus.Active);
            depleted.Deplete(From);
            db.Flocks.Add(depleted);

            // A movement on a flock that is NOT the one the entry-name tests use, so
            // the bounded-aggregate guard has a value that must stay OUT of a page
            // that excludes this flock, and a value that must appear when it is in.
            db.BirdMovements.Add(BirdMovement.Create(
                Guid.NewGuid(), accountId, f.MovementFlock, From,
                BirdMovementType.Mortality, 37, null));

            foreach (var id in new[] { f.CustomerA, f.CustomerC })
                db.Customers.Add(Customer.Create(
                    id, accountId, id == f.CustomerA ? f.CustomerAName : f.CustomerCName,
                    "555-0000"));

            // Fillers push the reference flocks/customers past discovery rank 49,
            // which is what makes the "outside the first page" guard non-vacuous.
            // The ranks are then MEASURED in that test, never assumed from here.
            for (var i = 0; i < 60; i++)
            {
                db.Flocks.Add(Build(Guid.NewGuid(), accountId, f.FillerName(i), FlockStatus.Active));
                db.Customers.Add(Customer.Create(
                    Guid.NewGuid(), accountId, $"{f.Marker} cfiller {i:D2}", "555-0100"));
            }

            db.InventoryItems.Add(InventoryItem.Create(
                f.ItemId, accountId, f.FarmId, $"{f.Marker} feed",
                InventoryCategory.Feed, "kg", Money.Zero("USD")));
            db.ExpenseCategories.Add(ExpenseCategory.Create(
                f.CategoryId, accountId, f.FarmId, $"{f.Marker} fuel"));

            db.DailyEntries.Add(DailyEntry.Create(
                f.EntryActive, accountId, f.FarmId, f.HouseId, f.ActiveFlock, From));
            db.DailyEntries.Add(DailyEntry.Create(
                f.EntryArchived, accountId, f.FarmId, f.HouseId, f.ArchivedFlock, From.AddMonths(1)));
            db.DailyEntries.Add(DailyEntry.Create(
                f.EntryDepleted, accountId, f.FarmId, f.HouseId, f.DepletedFlock, From.AddMonths(2)));

            db.WaterUsages.Add(WaterUsage.Create(
                f.WaterActive, accountId, f.ActiveFlock, From, 10m, "L",
                WaterSource.Well, null, null, DateTime.UtcNow));
            db.WaterUsages.Add(WaterUsage.Create(
                f.WaterArchived, accountId, f.ArchivedFlock, From.AddMonths(1), 11m, "L",
                WaterSource.Well, null, null, DateTime.UtcNow));

            db.FeedUsages.Add(FeedUsage.Create(
                f.FeedActive, accountId, f.ActiveFlock, f.ItemId, From, 5m, "kg",
                Money.Zero("USD"), DateTime.UtcNow));
            db.FeedUsages.Add(FeedUsage.Create(
                f.FeedArchived, accountId, f.ArchivedFlock, f.ItemId, From.AddMonths(1), 6m, "kg",
                Money.Zero("USD"), DateTime.UtcNow));

            db.SalesOrders.Add(SalesOrder.Create(
                f.OrderA, accountId, f.CustomerA, $"{f.Marker}-A", From, "USD"));
            db.SalesOrders.Add(SalesOrder.Create(
                f.OrderC, accountId, f.CustomerC, $"{f.Marker}-C", From.AddMonths(1), "USD"));

            db.Expenses.Add(Expense.Create(
                f.ExpenseActive, accountId, f.FarmId, f.CategoryId, From, "diesel",
                1200, "USD", 2, f.ActiveFlock));
            db.Expenses.Add(Expense.Create(
                f.ExpenseArchived, accountId, f.FarmId, f.CategoryId, From.AddMonths(1), "lime",
                1300, "USD", 2, f.ArchivedFlock));
            // The nullable case, seeded deliberately: a per-row `First()` projection
            // throws on this row, and it is the row an inner join drops.
            db.Expenses.Add(Expense.Create(
                f.ExpenseNone, accountId, f.FarmId, f.CategoryId, From.AddMonths(2), "misc",
                1400, "USD", 2, null));

            await db.SaveChangesAsync();
        });

        // The assignment rows go through the same tenant scope. A farm-wide row is
        // FarmId-scoped (the aggregate forbids an unscoped row), so its FlockId is
        // null — the shape the response must render as a null name rather than drop.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                f.FarmWideAssignment, accountId, f.WorkerId, f.FarmId, null, null));
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                f.FlockAssignment, accountId, f.WorkerId, null, null, f.ActiveFlock));
            await db.SaveChangesAsync();
        });

        return f;
    }

    // Built inside the tenant scope, so `accountId` is the scope's OWN tenant — the
    // interceptor stamps an empty id and rejects a non-empty one that differs, so
    // anything else here (a shared SeedDefaults constant, say) would throw for every
    // tenant but the default one. Passing the scope's tenant keeps the fixture
    // honest for the second tenant the isolation test seeds.
    private static Flock Build(Guid id, Guid accountId, string name, FlockStatus status)
    {
        var flock = Flock.Create(
            id, accountId, SeedDefaults.FarmId, SeedDefaults.HouseId,
            name, "ISA Brown", From, 100);
        switch (status)
        {
            case FlockStatus.Archived:
                flock.Archive(From);
                break;
            case FlockStatus.Depleted:
                flock.Deplete(From);
                break;
        }
        return flock;
    }

    // ── Contract: each row carries its own display names ──────────────────────

    [Fact]
    public async Task DailyEntryList_CarriesRowOwnedFlockNameAndStatus()
    {
        var (client, f) = await SeedAsync();
        var rows = await GetRows<DailyEntryRow>(client, RouteEntries);
        var byId = rows.ToDictionary(r => r.Id);

        Assert.Equal(f.ActiveName, byId[f.EntryActive].FlockName);
        Assert.Equal(Active, byId[f.EntryActive].FlockStatus);
        // The two rows a picker-eligibility shortcut would lose.
        Assert.Equal(f.ArchivedName, byId[f.EntryArchived].FlockName);
        Assert.Equal(Archived, byId[f.EntryArchived].FlockStatus);
        Assert.Equal(f.DepletedName, byId[f.EntryDepleted].FlockName);
        Assert.Equal(Depleted, byId[f.EntryDepleted].FlockStatus);

        Assert.All(f.OwnedEntryIds(), id => AssertFragmentFree(byId[id].FlockName));
    }

    [Fact]
    public async Task DailyEntryDetail_CarriesRowOwnedFlockNameAndStatus()
    {
        // Detail and list must answer IDENTICALLY — a name that only the list route
        // computes leaves the SPA's edit form nameless.
        var (client, f) = await SeedAsync();
        var entry = await client.GetFromJsonAsync<DailyEntryRow>(
            $"/api/v1/{RouteEntries}/{f.EntryArchived}");
        Assert.NotNull(entry);
        Assert.Equal(f.ArchivedName, entry.FlockName);
        Assert.Equal(Archived, entry.FlockStatus);
    }

    [Fact]
    public async Task FeedUsageList_CarriesRowOwnedFlockName_IncludingArchived()
    {
        var (client, f) = await SeedAsync();
        var rows = await GetRows<FeedRow>(client, RouteFeed);
        var byId = rows.ToDictionary(r => r.Id);

        Assert.Equal(f.ActiveName, byId[f.FeedActive].FlockName);
        Assert.Equal(f.ArchivedName, byId[f.FeedArchived].FlockName);
        AssertFragmentFree(byId[f.FeedActive].FlockName);
    }

    [Fact]
    public async Task WaterUsageList_CarriesRowOwnedFlockName_IncludingArchived()
    {
        var (client, f) = await SeedAsync();
        var rows = await GetRows<WaterRow>(client, RouteWater);
        var byId = rows.ToDictionary(r => r.Id);

        Assert.Equal(f.ActiveName, byId[f.WaterActive].FlockName);
        Assert.Equal(f.ArchivedName, byId[f.WaterArchived].FlockName);
        AssertFragmentFree(byId[f.WaterActive].FlockName);
    }

    [Fact]
    public async Task FlockAssignments_NameEachAssignmentAndLeaveFarmWideBlank()
    {
        var (client, f) = await SeedAsync();
        var rows = await GetRows<AssignmentRow>(client, $"users/{f.WorkerId}/flock-assignments");
        var byId = rows.ToDictionary(r => r.Id);

        Assert.Equal(2, rows.Count);
        Assert.Equal(f.ActiveName, byId[f.FlockAssignment].FlockName);
        // The farm-wide row SURVIVES with a null name. An inner join deletes it, and
        // a deleted row reads as "this worker has no farm-wide access" — the exact
        // opposite of the truth.
        Assert.Null(byId[f.FarmWideAssignment].FlockId);
        Assert.Null(byId[f.FarmWideAssignment].FlockName);
    }

    [Fact]
    public async Task SalesOrderListAndDetail_CarryRowOwnedCustomerName()
    {
        var (client, f) = await SeedAsync();
        var rows = await GetRows<SalesRow>(client, RouteSales);
        var byId = rows.ToDictionary(r => r.Id);

        Assert.Equal(f.CustomerAName, byId[f.OrderA].CustomerName);
        Assert.Equal(f.CustomerCName, byId[f.OrderC].CustomerName);
        Assert.All(f.OwnedOrderIds(), id => AssertFragmentFree(byId[id].CustomerName));

        var detail = await client.GetFromJsonAsync<SalesRow>($"/api/v1/{RouteSales}/{f.OrderC}");
        Assert.NotNull(detail);
        Assert.Equal(f.CustomerCName, detail.CustomerName);
    }

    [Fact]
    public async Task ExpenseListAndDetail_CarryNullableFlockName()
    {
        var (client, f) = await SeedAsync();
        var list = await client.GetFromJsonAsync<ExpenseList>($"/api/v1/{RouteExpenses}?limit=500");
        Assert.NotNull(list);
        var byId = list.Items.ToDictionary(r => r.Id);

        Assert.Equal(f.ActiveName, byId[f.ExpenseActive].FlockName);
        Assert.Equal(f.ArchivedName, byId[f.ExpenseArchived].FlockName);
        Assert.Null(byId[f.ExpenseNone].FlockId);
        Assert.Null(byId[f.ExpenseNone].FlockName);   // not-attributed, not unresolved
        AssertFragmentFree(byId[f.ExpenseActive].FlockName!);
        var detail = await client.GetFromJsonAsync<ExpenseRow>(
            $"/api/v1/{RouteExpenses}/{f.ExpenseArchived}");
        Assert.NotNull(detail);
        Assert.Equal(f.ArchivedName, detail.FlockName);
    }

    [Fact]
    public async Task ExpenseAdjustResponse_CarriesFlockName()
    {
        // The adjust route returns the corrected row for rebinding, so a response
        // without the name puts the edit form back on a nameless row.
        var (client, f) = await SeedAsync();
        var before = await client.GetFromJsonAsync<ExpenseRow>(
            $"/api/v1/{RouteExpenses}/{f.ExpenseNone}");
        Assert.NotNull(before);
        Assert.Null(before.FlockName);

        var adjusted = await client.PutWithKeyAsync(
            $"/api/v1/{RouteExpenses}/{f.ExpenseNone}", Guid.NewGuid().ToString(), new
            {
                version = before.Version,
                expenseCategoryId = f.CategoryId,
                date = new DateOnly(2026, 5, 14),
                description = "adjusted",
                amountMinorUnits = 3500L,
                flockId = f.ArchivedFlock,
                note = (string?)null,
            });
        Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
        var row = await adjusted.Content.ReadFromJsonAsync<ExpenseRow>();
        Assert.NotNull(row);
        Assert.Equal(f.ArchivedFlock, row.FlockId);
        Assert.Equal(f.ArchivedName, row.FlockName);
    }

    // ── Names are CURRENT, not snapshotted ───────────────────────────────────

    [Fact]
    public async Task RowNames_ReflectTheCurrentFlockName()
    {
        var (client, f) = await SeedAsync();
        const string renamed = "pj renamed flock target";
        await factory.WithTenantScopeAsync(f.AccountId, async db =>
        {
            // A direct EF write, so nothing in the assertion below can be credited
            // to a write-path side effect (a rename through the API would also have
            // to be audited, which is a different contract).
            await db.Flocks.Where(x => x.Id == f.ActiveFlock)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, renamed));
        });

        var entries = await GetRows<DailyEntryRow>(client, RouteEntries);
        Assert.Equal(renamed, entries.Single(r => r.Id == f.EntryActive).FlockName);
        var feed = await GetRows<FeedRow>(client, RouteFeed);
        Assert.Equal(renamed, feed.Single(r => r.Id == f.FeedActive).FlockName);
    }

    [Fact]
    public async Task SalesRowName_ReflectsTheCurrentCustomerName()
    {
        var (client, f) = await SeedAsync();
        const string renamed = "pj renamed customer target";
        await factory.WithTenantScopeAsync(f.AccountId, async db =>
            await db.Customers.Where(x => x.Id == f.CustomerA)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, renamed)));

        var sales = await GetRows<SalesRow>(client, RouteSales);
        Assert.Equal(renamed, sales.Single(r => r.Id == f.OrderA).CustomerName);
    }

    // ── A reference is not a picker result ───────────────────────────────────

    [Fact]
    public async Task RowNames_ResolveForReferencesOutsideTheFirstDiscoveryPage()
    {
        var (client, f) = await SeedAsync();

        // THE PREMISE, MEASURED. This test is only meaningful when the named
        // references are unreachable inside the first discovery page; if the
        // fixture ever arranges them early, the assertions below would pass while
        // proving nothing. So the ranks are read from the routes' own ordering
        // rather than arranged by hand — hand-picking a name that "sorts last" is
        // not portable (this database collates with ICU, which ignores punctuation
        // and orders letters before digits, so both "customer zz01" and
        // "customer ~z" still ranked FIRST).
        var flockRanks = await RanksAsync(client, "/api/v1/flocks", f.Marker);
        var customerRanks = await RanksAsync(client, "/api/v1/customers", f.Marker);
        Assert.True(flockRanks.TryGetValue(f.ActiveName, out var flockRank) && flockRank >= 50,
            $"flock reference ranked {flockRank} of {flockRanks.Count}; premise needs >= 50");
        Assert.True(customerRanks.TryGetValue(f.CustomerAName, out var customerRank) && customerRank >= 50,
            $"customer reference ranked {customerRank} of {customerRanks.Count}; premise needs >= 50");

        // And the reference stays reachable in depth, so the projection is not
        // merely reading a row that discovery could never return either.
        Assert.Contains(
            (await GetRows<NameRow>(client, "flocks")).Select(r => r.Name),
            n => n == f.ActiveName);

        var entries = await GetRows<DailyEntryRow>(client, RouteEntries);
        Assert.Equal(f.ActiveName, entries.Single(r => r.Id == f.EntryActive).FlockName);

        var sales = await GetRows<SalesRow>(client, RouteSales);
        Assert.Equal(f.CustomerAName, sales.Single(r => r.Id == f.OrderA).CustomerName);
    }

    // Every visible row's rank in a discovery route's own ordering.
    private static async Task<Dictionary<string, int>> RanksAsync(
        HttpClient client, string route, string marker)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var offset = 0; ; offset += 50)
        {
            var page = await client.GetFromJsonAsync<List<NameRow>>(
                $"{route}?limit=50&offset={offset}&search={Q(marker)}");
            Assert.NotNull(page);
            if (page.Count == 0) break;
            foreach (var row in page) ranks.TryAdd(row.Name, ranks.Count);
            if (page.Count < 50) break;
        }
        Assert.True(ranks.Count > 50, "fixture too small to have a first discovery page");
        return ranks;
    }

    // ── Scope is enforced by the query ───────────────────────────────────────

    // A row must never name another tenant's entity, and must never LOSE its own
    // name because a foreign id entered the same read. Both halves matter: an
    // assertion that only scans A's responses for foreign names passes when the
    // reference read bypasses its filter, because the ids A ever sends are A's own —
    // the bypass is invisible until you ask what the read WOULD have returned.
    //
    // So the test is driven where the mutation lives: both tenants' keys, in one
    // read, addressed as one tenant. The filter's job is to resolve exactly that
    // tenant's own rows.
    [Fact]
    public async Task BulkReferenceReads_AreTenantScopedNotResponseScoped()
    {
        var (clientA, fa) = await SeedAsync();
        // B is a REAL account, not a bare Guid: a read that keys off the tenants
        // table (or an account that only exists as an id) would make "B's rows did not
        // resolve" true for the wrong reason. SeedAccountWithUserAsync gives B an
        // Account row, a user, and the packed-unit defaults, exactly like A.
        var accountB = await factory.SeedAccountWithUserAsync($"pj-b-{Guid.NewGuid():N}@test.local");
        var fb = await SeedGraphAsync(accountB);

        // Both tenants' keys in one request — the shape a permissive read would take
        // bait on, and the shape a page can legitimately produce if a foreign id ever
        // reached a row.
        var flockIds = new[] { fa.ActiveFlock, fa.ArchivedFlock, fb.ActiveFlock };
        var customerIds = new[] { fa.CustomerA, fa.CustomerC, fb.CustomerA };

        var flocks = await FlockNamesAsync(fa.AccountId, flockIds);
        var customers = await CustomerNamesAsync(fa.AccountId, customerIds);

        // A's own keys resolve, including Archived — the eligibility-hidden shape a
        // picker-shaped filter would drop.
        Assert.Equal(fa.ActiveName, flocks[fa.ActiveFlock].Name);
        Assert.Equal(fa.ArchivedName, flocks[fa.ArchivedFlock].Name);
        Assert.Equal(fa.CustomerAName, customers[fa.CustomerA].Name);
        Assert.Equal(fa.CustomerCName, customers[fa.CustomerC].Name);

        // B's keys do NOT resolve from A, for EITHER aggregate. These two are the
        // assertions that redden an IgnoreQueryFilters() mutation on the respective
        // read; scanning A's responses for B's names does not, because A never
        // sends B's ids.
        Assert.False(flocks.ContainsKey(fb.ActiveFlock));
        Assert.False(customers.ContainsKey(fb.CustomerA));

        // CONTROL — the same keys DO resolve for their own tenant, on both reads, so
        // the absence above is the filter working and not an addressable-nothing
        // fixture. And not the other way round.
        var flocksForB = await FlockNamesAsync(accountB, [fb.ActiveFlock, fa.ActiveFlock]);
        var customersForB = await CustomerNamesAsync(accountB, [fb.CustomerA, fa.CustomerA]);
        Assert.Equal(fb.ActiveName, flocksForB[fb.ActiveFlock].Name);
        Assert.Equal(fb.CustomerAName, customersForB[fb.CustomerA].Name);
        Assert.False(flocksForB.ContainsKey(fa.ActiveFlock));
        Assert.False(customersForB.ContainsKey(fa.CustomerA));

        // The HTTP responses agree: A names its own and nothing of B's.
        var entries = await GetRows<DailyEntryRow>(clientA, RouteEntries);
        Assert.All(entries, r => Assert.DoesNotContain(r.FlockName,
            new[] { fb.ActiveName, fb.ArchivedName, fb.DepletedName }));
    }

    // The flock reference read is scoped by TWO filters, and tenant isolation is only
    // one of them. A Worker narrowed to a subset of flocks must not name a flock
    // outside that subset — the same #613 predicate that guards the list route has to
    // guard this bulk read too, because a row projection reachable by a Worker is a
    // read surface like any other. Driven at the repository for the reason the
    // assignment test is: the route behind it is Owner-only.
    [Fact]
    public async Task FlockReferenceRead_RespectsFlockScopeForAWorker()
    {
        var (_, f) = await SeedAsync();

        // Narrowed to the Active flock only. The three asks are: that flock (in
        // scope, must name), the Archived one (tenant's own, out of scope, must not),
        // and the Movement one (also out of scope).
        var flocks = await factory.WithTenantScopeAsync(f.AccountId, async db =>
        {
            db.FlockScope.Resolve(unrestricted: false, [f.ActiveFlock]);
            return await new Cluckwork.Infrastructure.Repositories.FlockRepository(db)
                .GetDisplayNamesAsync([f.ActiveFlock, f.ArchivedFlock, f.MovementFlock]);
        });

        Assert.Equal(f.ActiveName, flocks[f.ActiveFlock].Name);       // in scope, named
        Assert.False(flocks.ContainsKey(f.ArchivedFlock));            // out of scope
        Assert.False(flocks.ContainsKey(f.MovementFlock));            // out of scope

        // CONTROL — the same three ids, unrestricted, all three resolve. Without this
        // the nulls above could be a fixture whose flocks no read ever reaches.
        var unrestricted = await FlockNamesAsync(
            f.AccountId, [f.ActiveFlock, f.ArchivedFlock, f.MovementFlock]);
        Assert.Equal(3, unrestricted.Count);
        Assert.Equal(f.ArchivedName, unrestricted[f.ArchivedFlock].Name);
    }

    // A Worker is scoped to assigned flocks. The projection's flock half is joined
    // through the FILTERED Flocks set, so a Worker must not learn the name of a
    // flock they were never assigned — while the assignment row itself still
    // appears. Dropping the row would read as "this worker has no such assignment",
    // which is a different (and wrong) fact.
    //
    // Driven at the repository, not over HTTP: the route is Owner-only
    // (`RequireAuthorization(OwnerOnly)` on the group), so a Worker cannot reach it,
    // and standing up an Owner to read another user's list would test the Owner's
    // unrestricted scope rather than the projection. FlockScope.Resolve is the same
    // call the request middleware makes, so the filter composes over the same
    // predicate a scoped request would carry — and the control below proves the
    // unfiltered query really WOULD have named the flock, which is what makes the
    // null an observation about the filter rather than about missing data.
    [Fact]
    public async Task AssignmentProjection_RespectsFlockScopeForAWorker()
    {
        var (client, f) = await SeedAsync();

        using var scope = factory.Services.CreateScope()
            .ResolveTenantAndActor(f.AccountId, f.WorkerId, "worker@test.local", roles: []);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The Worker's real scope: narrowed to one flock, exactly as a user with one
        // assignment row resolves it.
        // Narrowed to the marker flock: the graph's OTHER flocks become invisible,
        // which is what the projection must then refuse to name.
        db.FlockScope.Resolve(unrestricted: false, [f.ArchivedFlock]);

        var repo = scope.ServiceProvider
            .GetRequiredService<Cluckwork.Application.Features.Users.IUserRoleAssignmentRepository>();
        var scoped = await repo.ListByNameByUserAsync(f.WorkerId);

        // Both rows survive, and NEITHER may be named: the marker flock has no
        // assignment row (so no row carries its id), and the Active flock the rows do
        // name is outside this scope.
        Assert.Equal(2, scoped.Count);
        Assert.All(scoped, r => Assert.Null(r.FlockName));

        // CONTROL — the same read without the model filters names everything, so the
        // nulls above are the filter and not an unresolvable join.
        var unfiltered = await db.UserRoleAssignments.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.UserId == f.WorkerId)
            .Join(db.Flocks.AsNoTracking().IgnoreQueryFilters(),
                a => a.FlockId, fl => (Guid?)fl.Id,
                (a, fl) => new { a.Id, fl.Name })
            .ToListAsync();
        // The control's real claim: with no filters, the Active flock the assignment
        // names IS resolvable — so the nulls above came from the scope, not from a
        // join that matches nothing.
        Assert.Single(unfiltered, x => x.Name == f.ActiveName);

        // And the Owner's unrestricted view names both, so the projection is not
        // simply failing to resolve anything.
        var admin = await GetRows<AssignmentRow>(client, $"users/{f.WorkerId}/flock-assignments");
        Assert.Equal(2, admin.Count);
        Assert.Single(admin, r => r.FlockId == null && r.FlockName == null);   // farm-wide
        Assert.Single(admin, r => r.FlockId == f.ActiveFlock
            && r.FlockName == f.ActiveName);                                   // named
    }

    // ── Query-shape guards ───────────────────────────────────────────────────
    // Each window opens AFTER a warm-up request, so a first-execution plan query
    // cannot be counted as a read, and asserts both the tagged read's execution
    // count and the absence of per-row reference statements.

    // Probe honesty, asserted once for all four reads rather than four times. Every
    // count below is keyed on a tag, so an instrument that silently sees NOTHING
    // would report "exactly one read" for a route that makes none — the worst
    // guard failure there is, because it reads as a pass. EF's TagWith folds tags
    // into a LEADING comment block and Npgsql reports CommandText without leading
    // comments, so this is a real possibility, not a theoretical one.
    [Fact]
    public async Task ProbeSeesEveryTaggedRead()
    {
        var (client, _) = await SeedAsync();

        using (var w = Probe.Arm(ShapeProbe.FlockReference))
        {
            await GetRows<DailyEntryRow>(client, RouteEntries);
            Assert.NotEmpty(w.Marked);
        }
        using (var w = Probe.Arm(ShapeProbe.CustomerReference))
        {
            await GetRows<SalesRow>(client, RouteSales);
            Assert.NotEmpty(w.Marked);
        }
        using (var w = Probe.Arm(ShapeProbe.MovementAggregate))
        {
            await GetRows<FlockRow>(client, "flocks");
            Assert.NotEmpty(w.Marked);
        }
        using (var w = Probe.Arm(ShapeProbe.AssignmentProjection))
        {
            await GetRows<AssignmentRow>(client, "users/00000000-0000-0000-0000-000000000001/flock-assignments");
            Assert.NotEmpty(w.Marked);
        }
    }

    // One grouped reference read per page, on every flock-naming route. A per-row
    // lookup — the N+1 the contract forbids — multiplies this.
    [Theory]
    [InlineData(RouteEntries)]
    [InlineData(RouteFeed)]
    [InlineData(RouteWater)]
    public async Task FlockReferences_AreReadOncePerPageOnEveryRoute(string route)
    {
        var (client, _) = await SeedAsync();
        await GetRows<WaterRow>(client, route);       // warm (shape-agnostic: only the
                                                      // request matters here)

        using var probe = Probe.Arm(ShapeProbe.FlockReference);
        var rows = await GetRows<WaterRow>(client, route);
        Assert.NotNull(rows);

        Assert.Single(probe.Marked);
        Assert.All(probe.Marked, sql => Assert.Contains("flocks", sql, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DailyEntryList_ReadsFlockReferencesOncePerPage()
    {
        var (client, f) = await SeedAsync();
        await GetRows<DailyEntryRow>(client, RouteEntries);      // warm

        using var probe = Probe.Arm(ShapeProbe.FlockReference);
        var rows = await GetRows<DailyEntryRow>(client, RouteEntries);
        AssertRows(rows, f);
        Assert.Single(probe.Marked);
        // BOUNDED TO THE PAGE, asserted as a count: the read's parameter set is the
        // distinct flock ids the page returned, so a read that bound nothing (and so
        // would scan every visible flock) reddens here. Deliberately not an
        // "every fixture id appears" claim — the route is paged, and asserting that
        // would make the guard depend on which rows a page happens to hold.
        Assert.Equal(rows.Select(r => r.FlockId).Distinct().Count(), probe.MarkedParameterCounts[0]);
        // And the bound binds to SOMETHING real: at least one bound id is one this
        // page names, so a garbage predicate cannot satisfy the count above.
        Assert.Contains(probe.MarkedParameters[0].Split('\n', StringSplitOptions.RemoveEmptyEntries),
            p => rows.Any(r => r.FlockId.ToString("D") == p));
    }

    // One tagged read per page, at two page sizes — and the two pages must ACTUALLY
    // differ, on both the row count and the number of distinct flock ids the read
    // bound. Without those two strict inequalities the guard is vacuous: on a fixture
    // whose rows all share one flock, a limit-2 page and a limit-500 page bound the
    // same single id and "the read ran once either way" says nothing about whether
    // the read scales with the page. (The first version of this test used water
    // usage, where that is exactly what happened.)
    [Fact]
    public async Task FlockReferenceReadCount_DoesNotGrowWithRowCount()
    {
        var (client, f) = await SeedAsync();
        // Entries spread over several flocks, so a bigger page really is a bigger
        // bound, not just more rows naming the same flock.
        await AddEntriesAsync(f, [("pf entry m1", f.MovementFlock), ("pf entry a1", f.ArchivedFlock),
            ("pf entry a2", f.ArchivedFlock), ("pf entry d1", f.DepletedFlock)]);
        await GetRows<DailyEntryRow>(client, RouteEntries);   // warm

        // A paged request goes through GetPagedAsync: GetRows appends its own limit,
        // and an explicit one becoming `?limit=2?limit=500` is a 400 that reads like
        // a route bug rather than a test bug.
        using var small = Probe.Arm(ShapeProbe.FlockReference);
        var smallRows = await GetPagedAsync<DailyEntryRow>(client, $"{RouteEntries}?limit=2");
        small.Dispose();

        using var big = Probe.Arm(ShapeProbe.FlockReference);
        var bigRows = await GetPagedAsync<DailyEntryRow>(client, $"{RouteEntries}?limit=500");
        big.Dispose();

        // THE PREMISES, ASSERTED — strictly, on both axes.
        Assert.True(bigRows.Count > smallRows.Count,
            $"pages did not differ in rows: {smallRows.Count} vs {bigRows.Count}");
        var smallBound = smallRows.Select(r => r.FlockId).Distinct().Count();
        var bigBound = bigRows.Select(r => r.FlockId).Distinct().Count();
        Assert.True(bigBound > smallBound,
            $"pages did not differ in distinct flocks: {smallBound} vs {bigBound}");
        // And the read really was bounded per page — otherwise the bound comparison
        // below is comparing two unbounded reads.
        Assert.Equal(smallBound, small.MarkedParameterCounts[0]);
        Assert.Equal(bigBound, big.MarkedParameterCounts[0]);

        // THE CLAIM: the read ran once per page regardless.
        Assert.Single(small.Marked);
        Assert.Single(big.Marked);
    }

    [Fact]
    public async Task SalesList_ReadsCustomerReferencesOncePerPage()
    {
        var (client, f) = await SeedAsync();
        await GetRows<SalesRow>(client, RouteSales);            // warm

        using var probe = Probe.Arm(ShapeProbe.CustomerReference);
        var rows = await GetRows<SalesRow>(client, RouteSales);
        Assert.Equal(new[] { f.CustomerAName, f.CustomerCName },
            rows.Select(r => r.CustomerName).Order().ToArray());

        Assert.Single(probe.Marked);
        // Bounded to the page's two customers — and here the fixture's orders ARE the
        // whole page, so the exact id set is assertable rather than merely counted.
        Assert.Equal(2, probe.MarkedParameterCounts[^1]);
        Assert.Contains(f.CustomerA.ToString("D"), probe.MarkedParameters[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(f.CustomerC.ToString("D"), probe.MarkedParameters[0], StringComparison.OrdinalIgnoreCase);
    }

    // Expenses' flock reference is NULLABLE, which is where a naive projection
    // breaks: a per-row `First()` throws on the unattributed row. The fixture seeds
    // that row deliberately, so a broken implementation cannot hide behind the rows
    // that do resolve.
    [Fact]
    public async Task ExpenseList_ResolvesNullableFlockWithoutPerRowReads()
    {
        var (client, f) = await SeedAsync();
        await client.GetAsync($"/api/v1/{RouteExpenses}?limit=500");   // warm

        using var probe = Probe.Arm(ShapeProbe.FlockReference);
        var rows = (await client.GetFromJsonAsync<ExpenseList>(
            $"/api/v1/{RouteExpenses}?limit=500"))!.Items;
        Assert.Equal(f.ActiveName, rows.Single(r => r.Id == f.ExpenseActive).FlockName);
        Assert.Contains(rows, r => r.FlockName is null);

        // The null row costs no reference query, and the attributed ones share one:
        // a window with exactly one flock statement is the whole claim.
        Assert.Single(probe.Statements,
            s => s.Contains("flocks", StringComparison.OrdinalIgnoreCase));
    }

    // #512 T047 — the guard that separates one LEFT JOIN from a per-row lookup.
    [Fact]
    public async Task AssignmentProjection_IsASingleLeftJoinStatement()
    {
        var (client, f) = await SeedAsync();
        await GetRows<AssignmentRow>(client, $"users/{f.WorkerId}/flock-assignments");

        using var probe = Probe.Arm(ShapeProbe.AssignmentProjection);
        var rows = await GetRows<AssignmentRow>(client, $"users/{f.WorkerId}/flock-assignments");
        Assert.Equal(2, rows.Count);

        var sql = probe.Marked[0];
        // LEFT JOIN present; the per-row constructs absent. The obvious-looking
        // `Select(a => db.Flocks.Where(...).FirstOrDefault())` returns IDENTICAL data
        // while rendering a correlated lookup per row, so only the statement separates
        // them. The constructs of a correlated lookup are a sub-SELECT in the projection list
        // and, for the set-returning form, LATERAL — matched on those, not on a table
        // alias or whitespace, which an EF/Npgsql upgrade can change with the property
        // intact.
        //
        // NOT asserted: an absence match on `LIMIT 1`. A real N+1's scalar LIMIT
        // reaches the server as a bound parameter (`LIMIT @p`), so a literal-text match
        // would pass against that shape — a guard that cannot fail for the case it
        // names is worse than no guard, so the claim is left where it actually holds:
        // the projection list contains no sub-SELECT.
        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LATERAL", sql, StringComparison.OrdinalIgnoreCase);
        var projectionList = sql[(sql.IndexOf("SELECT", StringComparison.Ordinal) + 6)..
            sql.IndexOf("FROM", StringComparison.Ordinal)];
        Assert.DoesNotContain("SELECT", projectionList, StringComparison.OrdinalIgnoreCase);

        // One execution of the tagged read, which is the `Marked` claim; the window's
        // other statements are excluded on purpose because one of them is the
        // middleware's credential-epoch read, which belongs to the request and not to
        // this projection.
        Assert.Single(probe.Marked);
    }

    // The flock list had this defect on `main`: it aggregated the caller's ENTIRE
    // visible movement ledger on every request, so cost grew with the farm's age
    // rather than with the page — #311's shape in a second hot path.
    //
    // Two observations, and the second is the causal one. Shape alone is weak: an
    // implementation can carry an id predicate that never binds. So the 37-bird
    // movement must stay OUT of a page that excludes its flock, and must be folded
    // IN on the page that includes it.
    [Fact]
    public async Task FlockList_MovementAggregationIsBoundedToReturnedFlocks()
    {
        var (client, f) = await SeedAsync();
        await GetRows<FlockRow>(client, "flocks");             // warm

        using var probe = Probe.Arm(ShapeProbe.MovementAggregate);
        var page = await client.GetFromJsonAsync<List<FlockRow>>(
            $"/api/v1/flocks?limit=1&search={Q(f.ActiveName)}");
        Assert.NotNull(page);
        var row = Assert.Single(page);
        Assert.Equal(100, row.CurrentBirds);                   // no foreign aggregate

        Assert.Single(probe.Marked);
        Assert.Contains("GROUP BY", probe.Marked[0], StringComparison.OrdinalIgnoreCase);
        // The aggregate's bound is its parameter list, and on this page that is the
        // ONE returned flock — not the account's whole visible flock set. That is the
        // unbounded bug, caught as a count rather than as a query dialect.
        Assert.Contains(row.Id.ToString("D"), probe.MarkedParameters[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, probe.MarkedParameterCounts[0]);
        // The other half of "bounded": a flock that owns a movement but is NOT on
        // this one-flock page must not appear in the bound set.
        Assert.DoesNotContain(f.MovementFlock.ToString("D"), probe.MarkedParameters[0],
            StringComparison.OrdinalIgnoreCase);

        // The bound binds: the flock that DOES own the movement shows 100 − 37.
        var withMovement = await client.GetFromJsonAsync<List<FlockRow>>(
            $"/api/v1/flocks?limit=1&search={Q(f.MovementName)}");
        Assert.NotNull(withMovement);
        Assert.Equal(63, Assert.Single(withMovement).CurrentBirds);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // The contract forbids an identifier fragment standing in for a name: a row that
    // could not resolve its reference must show a null, not half a Guid, because a
    // fragment reads as a fact the server never had.
    private static void AssertFragmentFree(string name)
    {
        Assert.False(string.IsNullOrWhiteSpace(name), "unresolved reference");
        Assert.False(name.Contains('-'), $"name looks like an identifier: {name}");
        Assert.False(name.Length == 36, $"name is a bare identifier: {name}");
    }

    private static void AssertRows(List<DailyEntryRow> rows, Fixture f)
    {
        var byId = rows.ToDictionary(r => r.Id);
        Assert.True(byId.ContainsKey(f.EntryArchived));      // fixture reached the page
        Assert.Equal(f.ActiveName, byId[f.EntryActive].FlockName);
    }

    // Adds entries against EXISTING flocks, so a caller can build a page whose
    // distinct-flock count differs from the base fixture's. Only the flock link and
    // date vary; the names here are for diagnosis, not for search.
    private async Task AddEntriesAsync(Fixture f,
        IEnumerable<(string Name, Guid FlockId)> extra)
    {
        var list = extra.ToList();
        await factory.WithTenantScopeAsync(f.AccountId, async db =>
        {
            for (var i = 0; i < list.Count; i++)
                db.DailyEntries.Add(DailyEntry.Create(
                    Guid.NewGuid(), f.AccountId, f.FarmId, f.HouseId, list[i].FlockId,
                    From.AddDays(100 + i)));
            await db.SaveChangesAsync();
        });
    }

    private sealed record NameRow(Guid Id, string Name);
    private sealed record FlockRow(Guid Id, string Name, long CurrentBirds);
    private sealed record DailyEntryRow(
        Guid Id, Guid FlockId, string FlockName, string FlockStatus);
    private sealed record FeedRow(Guid Id, Guid FlockId, string FlockName);
    private sealed record WaterRow(Guid Id, Guid FlockId, string FlockName);
    private sealed record AssignmentRow(Guid Id, Guid? FlockId, string? FlockName);
    // CustomerName non-null on purpose: every order here has a nameable customer, so
    // an unresolved reference fails instead of satisfying a nullable comparison.
    private sealed record SalesRow(Guid Id, Guid CustomerId, string CustomerName);
    private sealed record ExpenseRow(Guid Id, Guid? FlockId, string? FlockName, int Version);
    private sealed record ExpenseList(List<ExpenseRow> Items);
}

// Registers the observation interceptor for THIS suite only. ConfigureTestServices
// ADDS an AddDbContext callback rather than replacing the app's, so the app's own
// tenant-stamp interceptor and provider configuration stay in place and this one
// joins them. Production DI is untouched by anything in this file.
public sealed class NamedRowProjectionFactory : CluckworkWebApplicationFactory
{
    // ONE instance for this factory's lifetime: EF's interceptor is a singleton and
    // the DbContext options below resolve it from the root provider, so the object a
    // test arms is the object the pipeline observes. Constructed eagerly (not in
    // ConfigureWebHost) so it exists before any request and cannot differ per scope.
    public ShapeProbe Probe { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) =>
                options.AddInterceptors(Probe)));
    }
}
