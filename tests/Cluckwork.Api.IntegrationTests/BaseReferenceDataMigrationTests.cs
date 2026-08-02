namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

// #245 — what survives of MigrationUpgradePathTests after the InitialCreate
// squash.
//
// That file existed for #283's central UPGRADE guarantee: that
// AddBaseReferenceDataAndMustChangePassword applied cleanly on top of a
// database the OLD runtime DatabaseSeeder had already populated with random
// ids. The squash deletes both halves of that scenario — the migration and
// every migration it could be applied on top of — and the databases it
// protected (dev, CI, sim) are dropped and recreated as part of the same
// change, since their __EFMigrationsHistory no longer matches. There is no
// longer any starting state to upgrade FROM, so those two fixtures could not
// be made to run at all, let alone assert anything true.
//
// The third fixture is the one that outlives it, and it is exactly the
// question the squash raises: the reference data lives in raw SQL that a
// regenerated InitialCreate does NOT reproduce, so it had to be carried
// forward by hand. This proves the carry landed — a virgin database migrated
// to head really does end up with the default account, the four assignable
// roles, the ten default egg grades and the six packed-unit conversions,
// exactly one of each.
//
// No WebApplicationFactory: this drives EF's migrator directly against its
// own throwaway Testcontainers Postgres (the same pattern
// AppDbContextDesignTimeFactory uses to build an AppDbContext outside ASP.NET
// DI), so the database is genuinely untouched — nothing has booted the app
// against it, which is the whole point.
public sealed class BaseReferenceDataMigrationTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext());
    }

    [Fact]
    public async Task MigratingAVirginDatabase_ProducesExactlyOneOfEachReferenceRow()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        await db.Database.MigrateAsync();

        Assert.Equal(1, await db.Accounts.IgnoreQueryFilters()
            .CountAsync(a => a.Id == SeedDefaults.AccountId));
        Assert.Equal(4, await db.Roles.CountAsync(r => Roles.Assignable.Contains(r.Name)));
        Assert.Equal(10, await db.EggGrades.IgnoreQueryFilters()
            .CountAsync(g => g.AccountId == SeedDefaults.AccountId && g.FarmId == SeedDefaults.FarmId));
        Assert.Equal(6, await db.EggUnitConversions.IgnoreQueryFilters()
            .CountAsync(c => c.AccountId == SeedDefaults.AccountId));
    }

    // #245 — the expression unique indexes are raw SQL (EF cannot model a
    // functional index), so the squash's regenerated InitialCreate dropped
    // them until they were carried across by hand. MigrationSecurityReviewTests
    // checks the migration SOURCE contains them; this checks the DATABASE
    // actually ends up with them — the two halves a silent loss would have to
    // beat. Asserted through the constraint's observable behaviour: a
    // case-only-different duplicate name must be rejected.
    [Fact]
    public async Task MigratingAVirginDatabase_CreatesTheCaseInsensitiveUniqueIndexes()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        await db.Database.MigrateAsync();

        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'")
            .ToListAsync();

        Assert.Contains("IX_EggGrades_AccountId_FarmId_LowerName", indexes);
        Assert.Contains("IX_ExpenseCategories_NameCi", indexes);
        Assert.Contains("UX_InventoryItems_Account_Farm_LowerName", indexes);
        Assert.Contains("IX_Products_AccountId_LowerName", indexes);

        // Behavioural proof for one of them, so this isn't just a name check:
        // "SMALL" collides with the seeded "Small" only if the index really is
        // on lower("Name").
        var duplicate = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync($"""
            INSERT INTO "EggGrades" ("Id", "AccountId", "FarmId", "Name", "GradeType", "SortOrder", "IsSaleable", "Active", "Version")
            VALUES ({Guid.NewGuid()}, {SeedDefaults.AccountId}, {SeedDefaults.FarmId}, 'SMALL', 'Size', 99, TRUE, TRUE, 0)
            """));

        Assert.NotNull(duplicate);
        Assert.Contains("IX_EggGrades_AccountId_FarmId_LowerName", duplicate.ToString(), StringComparison.Ordinal);
    }
}
