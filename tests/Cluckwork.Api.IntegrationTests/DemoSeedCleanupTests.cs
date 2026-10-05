using System.Data.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

public sealed class DemoSeedCleanupFactory : CluckworkWebApplicationFactory
{
    public DemoSeedFaults Faults { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(Faults)));
    }
}

// Fails one statement on demand: the draft order's line (the demo's last write,
// after a confirmed sale and every daily entry have committed) and, optionally,
// one table's cleanup DELETE.
public sealed class DemoSeedFaults : DbCommandInterceptor
{
    private int _orderLines;

    public bool FailDraftLine { get; private set; }

    public string? FailDeleteFrom { get; private set; }

    public bool DraftLineFailed { get; private set; }

    public void Arm(bool failDraftLine, string? failDeleteFrom = null)
    {
        _orderLines = 0;
        DraftLineFailed = false;
        FailDraftLine = failDraftLine;
        FailDeleteFrom = failDeleteFrom;
    }

    public void Disarm() => (FailDraftLine, FailDeleteFrom) = (false, null);

    private void Check(DbCommand command)
    {
        if (FailDraftLine && command.CommandText.Contains("INSERT INTO \"SalesOrderItems\"", StringComparison.Ordinal)
            && Interlocked.Increment(ref _orderLines) == 3)
        {
            DraftLineFailed = true;
            throw new InvalidOperationException("injected: draft order line");
        }
        if (FailDeleteFrom is { } table
            && command.CommandText.StartsWith($"DELETE FROM \"{table}\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"injected: cleanup delete from {table}");
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }
}

// #858 P6 — DemoDataSeeder.CleanupPartialSeedAsync had no test. These pin what
// it removes after a late seed failure, that a failed DELETE rolls every earlier
// DELETE back, and that another farm's rows survive. Every read is a fresh
// context: ExecuteDelete bypasses tracked state.
[Collection(DemoSeedCollection.Name)]
public sealed class DemoSeedCleanupTests(DemoSeedCleanupFactory factory) : IClassFixture<DemoSeedCleanupFactory>
{
    private async Task<Guid> ProvisionFarmAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Cleanup Farm", $"cleanup-{suffix}", $"owner-{suffix}@example.test");
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : string.Empty);
        return result.Value.AccountId;
    }

    private async Task<SeedResult> SeedAsync(Guid accountId, bool failDraftLine, string? failDeleteFrom = null)
    {
        factory.Faults.Arm(failDraftLine, failDeleteFrom);
        try
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(accountId);
        }
        finally
        {
            factory.Faults.Disarm();
        }
    }

    // The nine tables CleanupPartialSeedAsync deletes from.
    private async Task<Dictionary<string, int>> CountRowsAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new Dictionary<string, int>
        {
            ["SalesOrderItems"] = await db.SalesOrderItems.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["SalesOrders"] = await db.SalesOrders.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["Customers"] = await db.Customers.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["BirdMovements"] = await db.BirdMovements.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["EggInventoryMovements"] = await db.EggInventoryMovements.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["EggLots"] = await db.EggLots.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["DailyEntryGrades"] = await db.DailyEntryGrades.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["DailyEntries"] = await db.DailyEntries.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
            ["Flocks"] = await db.Flocks.IgnoreQueryFilters().CountAsync(x => x.AccountId == accountId),
        };
    }

    [Fact]
    public async Task LateFailure_RemovesEveryFlockRootedAndCommerceRow()
    {
        var accountId = await ProvisionFarmAsync();

        var result = await SeedAsync(accountId, failDraftLine: true);

        Assert.Equal(SeedStatus.Failed, result.Status);
        Assert.True(factory.Faults.DraftLineFailed);
        Assert.All(await CountRowsAsync(accountId), row => Assert.Equal(0, row.Value));
    }

    [Fact]
    public async Task LateFailure_LeavesTheFarmReseedable()
    {
        var accountId = await ProvisionFarmAsync();
        Assert.Equal(SeedStatus.Failed, (await SeedAsync(accountId, failDraftLine: true)).Status);
        Assert.True(factory.Faults.DraftLineFailed);

        var retry = await SeedAsync(accountId, failDraftLine: false);

        Assert.True(retry.Status == SeedStatus.Seeded, retry.Message);
    }

    [Fact]
    public async Task FailedDelete_RollsBackEveryEarlierDelete()
    {
        var accountId = await ProvisionFarmAsync();

        var result = await SeedAsync(accountId, failDraftLine: true, failDeleteFrom: "Flocks");

        Assert.Equal(SeedStatus.Failed, result.Status);
        Assert.True(factory.Faults.DraftLineFailed);
        var rows = await CountRowsAsync(accountId);
        // The Commerce deletes ran first; their rows surviving is the rollback.
        Assert.All(rows, row => Assert.True(row.Value > 0, $"{row.Key} was emptied"));
    }

    [Fact]
    public async Task Cleanup_LeavesAnotherFarmsDemoRows()
    {
        var survivor = await ProvisionFarmAsync();
        Assert.Equal(SeedStatus.Seeded, (await SeedAsync(survivor, failDraftLine: false)).Status);
        var before = await CountRowsAsync(survivor);
        var accountId = await ProvisionFarmAsync();

        Assert.Equal(SeedStatus.Failed, (await SeedAsync(accountId, failDraftLine: true)).Status);
        Assert.True(factory.Faults.DraftLineFailed);

        Assert.Equal(before, await CountRowsAsync(survivor));
        Assert.All(before, row => Assert.True(row.Value > 0, $"{row.Key} had no rows to protect"));
        Assert.All(await CountRowsAsync(accountId), row => Assert.Equal(0, row.Value));
    }
}
