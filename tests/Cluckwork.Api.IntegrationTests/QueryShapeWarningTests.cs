using System.Reflection;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Insights;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class QueryShapeWarningTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task FactoryOptions_TwoSiblingCollectionIncludes_Throw()
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        await AssertMultipleIncludesThrowAsync(options);
    }

    [Fact]
    public async Task DirectOptions_TwoSiblingCollectionIncludes_Throw()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .ConfigureWarnings(QueryShapeWarnings.Configure)
            .Options;
        await AssertMultipleIncludesThrowAsync(options);
    }

    [Fact]
    public async Task DefaultOptions_TwoSiblingCollectionIncludes_KeepLogging()
    {
        var messages = new List<string>();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .LogTo(messages.Add, LogLevel.Warning)
            .Options;
        await using var db = new SiblingIncludeContext(options);
        await db.Set<IncludeOrder>().Where(o => o.Id == Guid.Empty)
            .Include(o => o.Items).Include(o => o.Allocations).ToListAsync();
        Assert.Contains(messages, message => message.Contains("MultipleCollectionIncludeWarning"));
    }

    [Fact]
    public async Task ExportSnapshot_UnorderedFirst_Throws()
    {
        using var scope = factory.Services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var export = new ExportQueries(request, new TenantContext(), new FlockScope());
        await using var snapshot = await export.BeginConsistentReadAsync();
        var db = Assert.IsType<AppDbContext>(typeof(ExportQueries)
            .GetField("activeDb", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(export));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").FirstAsync());
        Assert.Contains("FirstWithoutOrderByAndFilterWarning", exception.Message);
    }

    private static async Task AssertMultipleIncludesThrowAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new SiblingIncludeContext(options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Set<IncludeOrder>().Where(o => o.Id == Guid.Empty)
                .Include(o => o.Items).Include(o => o.Allocations).ToListAsync());
        Assert.Contains("MultipleCollectionIncludeWarning", exception.Message);
    }

    // AppDbContext has no sibling collection navigations. The probe maps existing tables.
    private sealed class SiblingIncludeContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options, new TenantContext(), new FlockScope())
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            foreach (var entity in builder.Model.GetEntityTypes().ToList())
                builder.Ignore(entity.ClrType);
            builder.Entity<IncludeOrder>().ToTable("SalesOrders");
            builder.Entity<IncludeItem>().ToTable("SalesOrderItems");
            builder.Entity<IncludeAllocation>().ToTable("SalesOrderAllocations");
            builder.Entity<IncludeOrder>().HasMany(o => o.Items)
                .WithOne().HasForeignKey(i => i.SalesOrderId);
            builder.Entity<IncludeOrder>().HasMany(o => o.Allocations)
                .WithOne().HasForeignKey(a => a.SalesOrderId);
        }
    }

    private sealed class IncludeOrder
    {
        public Guid Id { get; set; }
        public List<IncludeItem> Items { get; set; } = [];
        public List<IncludeAllocation> Allocations { get; set; } = [];
    }

    private sealed class IncludeItem
    {
        public Guid Id { get; set; }
        public Guid SalesOrderId { get; set; }
    }

    private sealed class IncludeAllocation
    {
        public Guid Id { get; set; }
        public Guid SalesOrderId { get; set; }
    }
}
