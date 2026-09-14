namespace Cluckwork.Application.Tests.Architecture;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class TableOwnerRealModelTests
{
    [Fact]
    public void RealModel_EveryTableAndCrossOwnerForeignKeyIsLedgered()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            .EnableServiceProviderCaching(false).Options;
        using var context = new AppDbContext(options, new TenantContext(), new FlockScope());
        var ledger = ModuleLedger.Load(Path.Combine(AppContext.BaseDirectory,
            "Architecture", "Data", "module-ledger.json"));

        var report = TableOwnerScanner.Scan(context.Model, ledger);
        var failures = TableOwnerScanner.Evaluate(report);

        Assert.True(failures.Count == 0, "table-owner guard failed:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void RealModel_WalksAtLeastThirtyDistinctTables()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            .EnableServiceProviderCaching(false).Options;
        using var context = new AppDbContext(options, new TenantContext(), new FlockScope());
        var ledger = ModuleLedger.Load(Path.Combine(AppContext.BaseDirectory,
            "Architecture", "Data", "module-ledger.json"));

        var report = TableOwnerScanner.Scan(context.Model, ledger);

        Assert.Equal(30, report.ExpectedTableCountFloor);
        Assert.True(report.WalkedTableCount >= 30, $"walked {report.WalkedTableCount} tables");
    }
}
