namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Expenses;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant, FlockScope flockScope)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    // #388 — exposed for the raw-SQL sites that bypass the query filters
    // (EggLotRepository's FOR UPDATE paths read the scope from here).
    public FlockScope FlockScope => flockScope;

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Flock> Flocks => Set<Flock>();
    public DbSet<BirdMovement> BirdMovements => Set<BirdMovement>();
    public DbSet<DailyEntry> DailyEntries => Set<DailyEntry>();
    public DbSet<DailyEntryGrade> DailyEntryGrades => Set<DailyEntryGrade>();
    public DbSet<EggGrade> EggGrades => Set<EggGrade>();
    public DbSet<EggLot> EggLots => Set<EggLot>();
    public DbSet<EggInventoryMovement> EggInventoryMovements => Set<EggInventoryMovement>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderItem> SalesOrderItems => Set<SalesOrderItem>();
    public DbSet<SalesOrderAllocation> SalesOrderAllocations => Set<SalesOrderAllocation>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryLot> InventoryLots => Set<InventoryLot>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<FeedUsage> FeedUsages => Set<FeedUsage>();
    public DbSet<WaterUsage> WaterUsages => Set<WaterUsage>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductEggGradeMapping> ProductEggGradeMappings => Set<ProductEggGradeMapping>();
    public DbSet<EggUnitConversion> EggUnitConversions => Set<EggUnitConversion>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<SimulationSeedState> SimulationSeedStates => Set<SimulationSeedState>();
    public DbSet<DurableJob> DurableJobs => Set<DurableJob>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<FarmLogo> FarmLogos => Set<FarmLogo>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        builder.ConfigureIdempotency();
        builder.ConfigureSimulationSeedState();
        builder.ConfigureDurableJobs();

        // Global query filters enforce tenant isolation on every read (tech spec §4.2).
        // A missing WHERE can never leak cross-tenant data.
        // Account.AccountId == Account.Id (self-reference), so the filter returns only the owning account.
        builder.Entity<Account>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        // #388 — flock-scope read filtering (INV-1). Combined with the tenancy
        // filter per the guidance: a second HasQueryFilter call would OVERWRITE
        // (not combine with) the first, silently deleting tenant isolation.
        // Flock: AccountId tenant isolation AND (unrestricted OR self id assigned).
        builder.Entity<Flock>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || flockScope.AssignedFlockIds.Contains(e.Id)));
        builder.Entity<BirdMovement>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || flockScope.AssignedFlockIds.Contains(e.FlockId)));
        builder.Entity<DailyEntry>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || flockScope.AssignedFlockIds.Contains(e.FlockId)));
        builder.Entity<DailyEntryGrade>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<EggGrade>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<EggLot>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || flockScope.AssignedFlockIds.Contains(e.FlockId)));
        builder.Entity<EggInventoryMovement>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<Customer>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<SalesOrder>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<SalesOrderItem>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<SalesOrderAllocation>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<Payment>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<InventoryItem>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<InventoryLot>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<InventoryMovement>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || e.FlockId == null
                || flockScope.AssignedFlockIds.Contains(e.FlockId.Value)));
        builder.Entity<FeedUsage>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || flockScope.AssignedFlockIds.Contains(e.FlockId)));
        builder.Entity<WaterUsage>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || flockScope.AssignedFlockIds.Contains(e.FlockId)));
        builder.Entity<ExpenseCategory>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<Expense>().HasQueryFilter(e =>
            e.AccountId == tenant.AccountId
            && (flockScope.IsUnrestricted || e.FlockId == null
                || flockScope.AssignedFlockIds.Contains(e.FlockId.Value)));
        builder.Entity<AuditEvent>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<Product>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<ProductEggGradeMapping>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<EggUnitConversion>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<UserRoleAssignment>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        builder.Entity<FarmLogo>().HasQueryFilter(e => e.AccountId == tenant.AccountId);
        // #279 review (codex): SimulationSeedState is keyed by AccountId, so it
        // gets the same tenant filter as every other AccountId-bearing entity
        // (AGENTS.md §Multi-tenancy). The seeder always resolves the tenant
        // before touching the row, so the filter is free defense-in-depth; the
        // TenantStampInterceptor leaves the explicit non-empty AccountId alone.
        builder.Entity<SimulationSeedState>().HasQueryFilter(e => e.AccountId == tenant.AccountId);

        // #532 — Identity's own indexes are GLOBAL: IdentityUserContext declares
        // UserNameIndex unique on NormalizedUserName alone, and EmailIndex
        // non-unique on NormalizedEmail alone. Adding a composite HasIndex does
        // NOT displace them, because the property lists differ — both survive,
        // and the surviving global unique index keeps rejecting the second
        // farm's copy of an email before Postgres ever evaluates the composite.
        //
        // WALK every index and remove the ones not led by AccountId, rather than
        // naming the two we happen to know about ("walk everything, exclude
        // deliberately" — AGENTS.md). GetIndexes() excludes the primary key and
        // alternate keys, so this cannot disturb them; that is asserted by
        // ApplicationUserIndexModelTests.ThePrimaryKey_SurvivesTheIndexWalk, so
        // do not add a carve-out.
        //
        // REUSING the database names "UserNameIndex" and "EmailIndex" is
        // LOAD-BEARING, not cosmetic. Verified by probe: with the names reused,
        // deleting the removal walk makes EF throw at model build ("both mapped
        // to 'AspNetUsers.EmailIndex', but with different columns") — every boot
        // and every test fails loudly. With the composites renamed to
        // IX_AspNetUsers_*, the same deletion builds SILENTLY with four indexes
        // and the whole slice is inert. Do not rename them.
        var user = builder.Entity<ApplicationUser>();
        foreach (var index in user.Metadata.GetIndexes().ToList())
        {
            if (index.Properties[0].Name != nameof(ApplicationUser.AccountId))
                user.Metadata.RemoveIndex(index);
        }

        // #532 review (owner) — Identity leaves all four of these nullable
        // because it supports username-only users with no email. This
        // application has none: every creation site sets UserName = email and
        // login IS by email, so optionality buys nothing and costs two things.
        //
        // First, Postgres allows MULTIPLE NULLS in a unique index, so a null
        // NormalizedUserName or NormalizedEmail sits OUTSIDE the composite
        // uniqueness this slice exists to enforce — a hole in the feature, not
        // merely defensive typing. Second, a stored null is a value the
        // validator would have to keep special-casing on the update path.
        //
        // Required here rather than by convention, so the database enforces it
        // instead of a comment claiming it cannot happen.
        user.Property(u => u.Email).IsRequired();
        user.Property(u => u.NormalizedEmail).IsRequired();
        user.Property(u => u.UserName).IsRequired();
        user.Property(u => u.NormalizedUserName).IsRequired();

        user.HasIndex(u => new { u.AccountId, u.NormalizedUserName })
            .HasDatabaseName("UserNameIndex")
            .IsUnique();
        user.HasIndex(u => new { u.AccountId, u.NormalizedEmail })
            .HasDatabaseName("EmailIndex")
            .IsUnique();

        // A real foreign key, with NO navigation property: a navigation from an
        // unfiltered dependent (ApplicationUser carries no query filter) to a
        // query-filtered principal (Account does, one screen up) is a query-time
        // hazard, and nothing needs the navigation.
        user.HasOne<Account>()
            .WithMany()
            .HasForeignKey(u => u.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // A user's farm is fixed at creation. Moving one between accounts would
        // carry its roles, password and lockout state across a tenant boundary.
        user.Property(u => u.AccountId).Metadata
            .SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        // #562 — AccountId is a CONCURRENCY TOKEN on every entity that carries
        // one. EF then puts AccountId's ORIGINAL value into the WHERE clause of
        // every UPDATE and DELETE it emits, beside the primary key (and beside
        // Version where the aggregate has one), so the database itself refuses
        // to touch a row whose AccountId is not the value the writer claimed.
        //
        // This is what closes the gap TenantStampInterceptor cannot close on its
        // own: the interceptor compares AccountId's original value against the
        // resolved tenant, but for an entity that reached SaveChanges DETACHED
        // (DbSet.Update / DbSet.Remove / Attach on a hand-built stub) that
        // original value is the caller's own, not the database's. The
        // interceptor still requires original == tenant, so with the token the
        // statement carries "AND AccountId = <tenant>" — a stub naming another
        // farm's row matches nothing and EF throws DbUpdateConcurrencyException.
        // Observed writing through before this walk existed, for Update, for
        // Remove, and for an owned-only edit with the principal Unchanged (which
        // the interceptor never even sees): DetachedTenantWriteTests.
        //
        // Discovered from the model, matched by NAME and CLR type exactly as the
        // interceptor matches (Entity<TId> or not — RefreshToken,
        // IdempotencyRecord and ApplicationUser are in scope). A primary-key
        // AccountId (SimulationSeedState) is excluded: the key is already in the
        // WHERE. AccountIdConcurrencyTokenModelTests pins the walk.
        //
        // The snapshot records the flag but EF emits no schema for it, so the
        // migration that accompanies this walk (AccountIdConcurrencyToken) is
        // deliberately empty — it exists to keep the snapshot equal to the model.
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var accountId = entityType.FindProperty(nameof(Entity<Guid>.AccountId));
            if (accountId is null || accountId.ClrType != typeof(Guid)) continue;
            if (accountId.IsPrimaryKey()) continue;
            accountId.IsConcurrencyToken = true;
        }
    }
}
