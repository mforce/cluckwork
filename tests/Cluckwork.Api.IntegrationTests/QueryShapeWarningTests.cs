using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class QueryShapeWarningTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task TwoSiblingCollectionIncludes_Throw()
    {
        await using var db = CreateProbe();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Set<IncludeOrder>().Where(o => o.Id == Guid.Empty)
                .Include(o => o.Items).Include(o => o.Allocations).ToListAsync());
        Assert.Contains("MultipleCollectionIncludeWarning", exception.Message);
    }

    [Fact]
    public void TranslationOnly_TwoSiblingCollectionIncludes_Throw()
    {
        using var db = CreateProbe();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            db.Set<IncludeOrder>().Where(o => o.Id == Guid.Empty)
                .Include(o => o.Items).Include(o => o.Allocations).ToQueryString());
        Assert.Contains("MultipleCollectionIncludeWarning", exception.Message);
    }

    [Fact]
    public async Task DefaultOptions_TwoSiblingCollectionIncludes_KeepLogging()
    {
        var messages = new List<string>();
        var options = new DbContextOptionsBuilder()
            .UseNpgsql(factory.ConnectionString)
            .LogTo(messages.Add, LogLevel.Warning)
            .Options;
        await using var db = new LoggingContext(options);
        await db.Set<IncludeOrder>().Where(o => o.Id == Guid.Empty)
            .Include(o => o.Items).Include(o => o.Allocations).ToListAsync();
        Assert.Contains(messages, message => message.Contains("MultipleCollectionIncludeWarning"));
    }

    private SiblingIncludeContext CreateProbe() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(factory.ConnectionString).Options);

    private static void ConfigureProbeModel(ModelBuilder builder)
    {
        builder.Entity<IncludeOrder>().ToTable("SalesOrders");
        builder.Entity<IncludeItem>().ToTable("SalesOrderItems");
        builder.Entity<IncludeAllocation>().ToTable("SalesOrderAllocations");
        builder.Entity<IncludeOrder>().HasMany(o => o.Items)
            .WithOne().HasForeignKey(i => i.SalesOrderId);
        builder.Entity<IncludeOrder>().HasMany(o => o.Allocations)
            .WithOne().HasForeignKey(a => a.SalesOrderId);
    }

    // AppDbContext has no sibling collection navigations. The probe maps existing tables.
    private sealed class SiblingIncludeContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options, new TenantContext(), new FlockScope())
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            foreach (var entity in builder.Model.GetEntityTypes().ToList())
                builder.Ignore(entity.ClrType);
            ConfigureProbeModel(builder);
        }
    }

    private sealed class LoggingContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => ConfigureProbeModel(builder);
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
