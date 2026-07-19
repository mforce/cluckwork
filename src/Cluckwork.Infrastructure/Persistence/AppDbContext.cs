namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Expenses;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Flock> Flocks => Set<Flock>();
    public DbSet<BirdMovement> BirdMovements => Set<BirdMovement>();
    public DbSet<DailyEntry> DailyEntries => Set<DailyEntry>();
    public DbSet<DailyEntryGrade> DailyEntryGrades => Set<DailyEntryGrade>();
    public DbSet<EggGrade> EggGrades => Set<EggGrade>();
    public DbSet<EggLot> EggLots => Set<EggLot>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderItem> SalesOrderItems => Set<SalesOrderItem>();
    public DbSet<SalesOrderAllocation> SalesOrderAllocations => Set<SalesOrderAllocation>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryLot> InventoryLots => Set<InventoryLot>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<FeedUsage> FeedUsages => Set<FeedUsage>();
    public DbSet<WaterUsage> WaterUsages => Set<WaterUsage>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<DurableJob> DurableJobs => Set<DurableJob>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        builder.ConfigureIdempotency();
        builder.ConfigureDurableJobs();

        // Global query filters enforce tenant isolation on every read (tech spec §4.2).
        // A missing WHERE can never leak cross-tenant data.
        // Account.AccountId == Account.Id (self-reference), so the filter returns only the owning account.
        builder.Entity<Account>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<Flock>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<BirdMovement>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<DailyEntry>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<DailyEntryGrade>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<EggGrade>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<EggLot>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<Customer>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<SalesOrder>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<SalesOrderItem>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<SalesOrderAllocation>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<InventoryItem>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<InventoryLot>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<InventoryMovement>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<FeedUsage>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<WaterUsage>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<ExpenseCategory>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<Expense>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
    }
}
