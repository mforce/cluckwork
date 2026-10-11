using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Cluckwork.Api.IntegrationTests.Mcp.McpConnection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #807 — the read tools through the real OAuth flow and the SDK's client: guard rows 26
// (a cross-farm bearer reads nothing), 31 (every reply is bounded and says so) and the
// name-resolution contract, plus #388's flock scope for a Worker.
[Collection(IntegrationCollection.Name)]
public sealed class McpReadToolsTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CrossFarmBearer_ReadsNothingOfTheOtherFarm()
    {
        using var host = Host(factory);
        var mine = await SeedAsync(factory, Roles.Manager);
        var theirs = await SeedAsync(factory, Roles.Manager);
        var myFlock = await SeedFlockAsync(mine.AccountId, "Mine");
        var theirFlock = await SeedFlockAsync(theirs.AccountId, "Theirs");
        var theirGrade = (await factory.SeedEggGradesAsync(theirs.AccountId, Guid.NewGuid(), "Theirs Large"))["Theirs Large"];
        var theirLot = await factory.SeedEggLotAsync(theirs.AccountId, theirGrade, 30);
        await SeedEntryAsync(theirs.AccountId, theirFlock, Today);

        await using var client = await ConnectAsync(host, await IssueAsync(host, mine, [OAuthScopes.ReadFarm]));

        var flocks = await PageAsync<FlockRow>(client, "cluckwork_list_flocks", new() { ["status"] = "all" });
        Assert.Equal([myFlock], flocks.Items.Select(f => f.FlockId));
        Assert.Empty((await PageAsync<StockRow>(client, "cluckwork_read_stock")).Items);
        Assert.Empty((await PageAsync<EggLotRow>(client, "cluckwork_list_egg_lots")).Items);
        Assert.Empty((await PageAsync<DailyEntryRow>(client, "cluckwork_list_daily_entries")).Items);
        Assert.Contains("No flock you can see",
            await ErrorAsync(client, "cluckwork_list_daily_entries", new() { ["flock"] = theirFlock.ToString() }));
        Assert.Contains("No flock you can see",
            await ErrorAsync(client, "cluckwork_list_daily_entries", new() { ["flock"] = "Theirs" }));
        Assert.Contains($"No egg lot {theirLot}",
            await ErrorAsync(client, "cluckwork_list_egg_lot_movements", new() { ["lotId"] = theirLot }));
        Assert.Contains("No egg grade",
            await ErrorAsync(client, "cluckwork_list_egg_lots", new() { ["grade"] = "Theirs Large" }));
    }

    [Fact]
    public async Task AssignedWorker_ReadsOnlyTheirFlocks()
    {
        using var host = Host(factory);
        var worker = await SeedAsync(factory, role: null);
        var assigned = await SeedFlockAsync(worker.AccountId, "Assigned");
        var other = await SeedFlockAsync(worker.AccountId, "Other");
        await SeedEntryAsync(worker.AccountId, assigned, Today);
        await SeedEntryAsync(worker.AccountId, other, Today);
        await factory.WithTenantScopeAsync(worker.AccountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(Guid.NewGuid(), worker.AccountId, worker.Id, null, null, assigned));
            await db.SaveChangesAsync();
        });

        await using var client = await ConnectAsync(host, await IssueAsync(host, worker, [OAuthScopes.ReadFarm]));

        var flocks = await PageAsync<FlockRow>(client, "cluckwork_list_flocks", new() { ["status"] = "all" });
        var entries = await PageAsync<DailyEntryRow>(client, "cluckwork_list_daily_entries");
        Assert.Equal([assigned], flocks.Items.Select(f => f.FlockId));
        Assert.Equal([assigned], entries.Items.Select(e => e.FlockId));
        Assert.Contains("No flock you can see",
            await ErrorAsync(client, "cluckwork_list_daily_entries", new() { ["flock"] = "Other" }));
    }

    // Duplicate flock names are legal, so a shared name is refused with every candidate's
    // id rather than resolved to whichever row comes first.
    [Fact]
    public async Task SharedFlockName_IsRefusedWithTheCandidates_AndAnIdResolvesIt()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var first = await SeedFlockAsync(user.AccountId, "House 2");
        var second = await SeedFlockAsync(user.AccountId, "House 2");
        var unique = await SeedFlockAsync(user.AccountId, "House 3");
        await SeedEntryAsync(user.AccountId, first, Today);
        await SeedEntryAsync(user.AccountId, second, Today);
        await SeedEntryAsync(user.AccountId, unique, Today);

        await using var client = await ConnectAsync(host, await IssueAsync(host, user, [OAuthScopes.ReadFarm]));

        var refusal = await ErrorAsync(client, "cluckwork_list_daily_entries", new() { ["flock"] = "House 2" });
        Assert.Contains($"{first} (Active)", refusal);
        Assert.Contains($"{second} (Active)", refusal);
        var byId = await PageAsync<DailyEntryRow>(client, "cluckwork_list_daily_entries", new() { ["flock"] = second.ToString() });
        var byName = await PageAsync<DailyEntryRow>(client, "cluckwork_list_daily_entries", new() { ["flock"] = "House 3" });
        Assert.Equal([second], byId.Items.Select(e => e.FlockId));
        Assert.Equal([unique], byName.Items.Select(e => e.FlockId));
        Assert.Equal("House 3", Assert.Single(byName.Items).FlockName);
    }

    // Row 31: each tool's first page says it is incomplete and where to continue, and the
    // continuation is the rest.
    [Theory]
    [InlineData("cluckwork_list_flocks")]
    [InlineData("cluckwork_read_stock")]
    [InlineData("cluckwork_list_egg_lots")]
    [InlineData("cluckwork_list_egg_lot_movements")]
    [InlineData("cluckwork_list_daily_entries")]
    public async Task EveryReadTool_PagesWithCompletenessAndContinuation(string tool)
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var grades = await factory.SeedEggGradesAsync(user.AccountId, Guid.NewGuid(), "Large", "Medium");
        var lot = await factory.SeedEggLotAsync(user.AccountId, grades["Large"], 40);
        await factory.SeedEggLotAsync(user.AccountId, grades["Medium"], 20);
        await factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), user.AccountId, lot, Domain.Modules.EggOperations.Contracts.EggMovementType.Sale,
                -5, "SalesOrder", Guid.NewGuid()));
            await db.SaveChangesAsync();
        });
        foreach (var name in new[] { "Paged A", "Paged B" })
            await SeedEntryAsync(user.AccountId, await SeedFlockAsync(user.AccountId, name), Today);

        await using var client = await ConnectAsync(host, await IssueAsync(host, user, [OAuthScopes.ReadFarm]));
        var arguments = tool == "cluckwork_list_egg_lot_movements" ? new Dictionary<string, object?> { ["lotId"] = lot } : [];

        var first = await PageAsync<JsonElement>(client, tool, new(arguments) { ["limit"] = 1 });
        Assert.Single(first.Items);
        Assert.False(first.Complete);
        Assert.Equal(1, first.NextOffset);

        var rest = await PageAsync<JsonElement>(client, tool, new(arguments) { ["limit"] = 1, ["offset"] = first.NextOffset });
        Assert.Single(rest.Items);
        Assert.True(rest.Complete);
        Assert.Null(rest.NextOffset);
        Assert.NotEqual(first.Items[0].GetRawText(), rest.Items[0].GetRawText());
    }

    // The tool's own cap holds when a caller asks for more.
    [Fact]
    public async Task OversizedLimit_IsCappedAndSaysSo()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        await factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            for (var i = 0; i <= McpPage.MaxLimit; i++)
                db.Flocks.Add(NewFlock(user.AccountId, $"Bulk {i:D3}"));
            await db.SaveChangesAsync();
        });

        await using var client = await ConnectAsync(host, await IssueAsync(host, user, [OAuthScopes.ReadFarm]));
        var page = await PageAsync<FlockRow>(client, "cluckwork_list_flocks", new() { ["limit"] = 10_000 });

        Assert.Equal(McpPage.MaxLimit, page.Items.Count);
        Assert.False(page.Complete);
        Assert.Equal(McpPage.MaxLimit, page.NextOffset);
    }

    // #388 for the egg reads: lots, their movements and the stock they sum to belong to a
    // flock, so a Worker sees only the assigned flock's, and an unassigned lot reads as missing.
    [Fact]
    public async Task AssignedWorker_ReadsOnlyTheirFlocksEggs()
    {
        using var host = Host(factory);
        var worker = await SeedAsync(factory, role: null);
        var assigned = await SeedFlockAsync(worker.AccountId, "Assigned");
        var other = await SeedFlockAsync(worker.AccountId, "Other");
        var grade = (await factory.SeedEggGradesAsync(worker.AccountId, Guid.NewGuid(), "Large"))["Large"];
        var mine = await SeedLotAsync(worker.AccountId, assigned, grade, 30);
        var theirs = await SeedLotAsync(worker.AccountId, other, grade, 70);
        await factory.WithTenantScopeAsync(worker.AccountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(Guid.NewGuid(), worker.AccountId, worker.Id, null, null, assigned));
            await db.SaveChangesAsync();
        });

        await using var client = await ConnectAsync(host, await IssueAsync(host, worker, [OAuthScopes.ReadFarm]));

        var stock = Assert.Single((await PageAsync<StockRow>(client, "cluckwork_read_stock")).Items);
        var lots = await PageAsync<EggLotRow>(client, "cluckwork_list_egg_lots");
        var movements = await PageAsync<EggLotMovementRow>(client, "cluckwork_list_egg_lot_movements", new() { ["lotId"] = mine });
        Assert.Equal(30, stock.Available);
        Assert.Equal([mine], lots.Items.Select(l => l.LotId));
        Assert.Equal([30], movements.Items.Select(m => m.QuantityDelta));
        var missing = Guid.NewGuid();
        var asMissing = (await ErrorAsync(client, "cluckwork_list_egg_lot_movements", new() { ["lotId"] = missing }))
            .Replace(missing.ToString(), "<lot>");
        var asTheirs = (await ErrorAsync(client, "cluckwork_list_egg_lot_movements", new() { ["lotId"] = theirs }))
            .Replace(theirs.ToString(), "<lot>");
        Assert.Contains("No egg lot <lot> exists on this farm.", asMissing);
        Assert.Equal(asMissing, asTheirs);
    }

    // Row 31 for the query, not only the reply: a page of a long ledger reads that page
    // from the database, counted as rows each reader actually returned.
    [Fact]
    public async Task LotMovementPage_ReadsOnlyThePageFromTheDatabase()
    {
        var reads = new ReaderRowCounts();
        using var host = WithReaderRowCounts(Host(factory), reads);
        var user = await SeedAsync(factory, Roles.Manager);
        var grade = (await factory.SeedEggGradesAsync(user.AccountId, Guid.NewGuid(), "Large"))["Large"];
        var lot = await factory.SeedEggLotAsync(user.AccountId, grade, 500);
        await factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            for (var i = 0; i < 40; i++)
                db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                    Guid.NewGuid(), user.AccountId, lot, Domain.Modules.EggOperations.Contracts.EggMovementType.Sale,
                    -1, "SalesOrder", Guid.NewGuid()));
            await db.SaveChangesAsync();
        });

        await using var client = await ConnectAsync(host, await IssueAsync(host, user, [OAuthScopes.ReadFarm]));
        reads.Clear();
        var page = await PageAsync<EggLotMovementRow>(client, "cluckwork_list_egg_lot_movements",
            new() { ["lotId"] = lot, ["limit"] = 2, ["offset"] = 10 });

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(12, page.NextOffset);
        // ReadCount includes the read that finds the end: the page, its lookahead row, and that.
        Assert.Equal([2 + 1 + 1], reads.From("\"EggInventoryMovements\""));
    }

    // However many flocks share a name, resolving it reads and lists a bounded number.
    [Fact]
    public async Task SharedFlockName_ListsBoundedCandidates_AndReadsBoundedRows()
    {
        var reads = new ReaderRowCounts();
        using var host = WithReaderRowCounts(Host(factory), reads);
        var user = await SeedAsync(factory, Roles.Manager);
        var shared = $"Reused {Guid.NewGuid():N}";
        await factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            for (var i = 0; i < 3 * Application.Modules.FlockManagement.Contracts.FlockNameResolution.MaxCandidates; i++)
                db.Flocks.Add(NewFlock(user.AccountId, shared));
            await db.SaveChangesAsync();
        });

        await using var client = await ConnectAsync(host, await IssueAsync(host, user, [OAuthScopes.ReadFarm]));
        reads.Clear();
        var refusal = await ErrorAsync(client, "cluckwork_list_daily_entries", new() { ["flock"] = shared });

        var max = Application.Modules.FlockManagement.Contracts.FlockNameResolution.MaxCandidates;
        Assert.Equal(max, refusal.Split("(Active)").Length - 1);
        Assert.Contains("More flocks share this name than are listed", refusal);
        // The candidates, the row proving more exist, and the read that finds the end.
        Assert.Equal([max + 1 + 1], reads.From("\"Flocks\""));
    }

    private static WebApplicationFactory<Program> WithReaderRowCounts(WebApplicationFactory<Program> host, ReaderRowCounts reads) =>
        host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(reads))));

    private async Task<Guid> SeedLotAsync(Guid accountId, Guid flockId, Guid gradeId, int quantity)
    {
        var lot = EggLot.Create(Guid.NewGuid(), accountId, flockId, Today, gradeId, quantity);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.EggLots.Add(lot);
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lot.Id, Domain.Modules.EggOperations.Contracts.EggMovementType.Production,
                quantity, "DailyEntry", Guid.NewGuid()));
            await db.SaveChangesAsync();
        });
        return lot.Id;
    }

    private async Task<Guid> SeedFlockAsync(Guid accountId, string name)
    {
        var flock = NewFlock(accountId, name);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Flocks.Add(flock);
            await db.SaveChangesAsync();
        });
        return flock.Id;
    }

    private static Flock NewFlock(Guid accountId, string name) =>
        Flock.Create(Guid.NewGuid(), accountId, Guid.NewGuid(), Guid.NewGuid(), name, "Test Breed", Today.AddDays(-30), 100);

    private Task SeedEntryAsync(Guid accountId, Guid flockId, DateOnly date) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = await db.Flocks.FindAsync(flockId);
            db.DailyEntries.Add(DailyEntry.Create(Guid.NewGuid(), accountId, flock!.FarmId, flock.HouseId, flockId, date));
            await db.SaveChangesAsync();
        });

    private static async Task<Page<T>> PageAsync<T>(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return JsonSerializer.Deserialize<Page<T>>(text, Json)!;
    }

    private static async Task<string> ErrorAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError == true, text);
        return text;
    }

    // McpPage<T> as a client reads it.
    private sealed record Page<T>(List<T> Items, bool Complete, int? NextOffset);

    // Rows each data reader returned, by the table its query selects from.
    private sealed class ReaderRowCounts : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<(string Sql, int Rows)> seen = new();

        public void Clear() => seen.Clear();

        public IReadOnlyList<int> From(string table) =>
            [.. seen.Where(r => r.Sql.Contains($"FROM {table}", StringComparison.Ordinal)).Select(r => r.Rows)];

        public override InterceptionResult DataReaderClosing(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            seen.Enqueue((command.CommandText, eventData.ReadCount));
            return result;
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            seen.Enqueue((command.CommandText, eventData.ReadCount));
            return ValueTask.FromResult(result);
        }
    }
}
