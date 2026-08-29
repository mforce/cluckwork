namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Eggs;
using Cluckwork.Api.IntegrationTests.Infrastructure;
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
        return new AppDbContext(options.Options, new TenantContext(), new FlockScope());
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

        Assert.NotNull(db.Model.FindEntityType(typeof(Account)));
        var accountEntityType = db.Model.FindEntityType(typeof(Account))!;
        var accountComparedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Account.Brand),
            nameof(Account.UnitSystem),
            nameof(Account.DefaultCurrencyMinorUnit),
            nameof(Account.DefaultStepperUnit),
            nameof(Account.IsActive),
            nameof(Account.FirstDayOfWeek),
            nameof(Account.DateFormatOverride),
            nameof(Account.TimeFormatOverride),
            nameof(Account.TimeZoneId),
            // #612 — same shape as UnitSystem/DefaultStepperUnit: a plain
            // default-value property, so the base-seeded row is compared
            // directly against Account.Create's own default.
            nameof(Account.WorkerSaleAllocationPolicy),
        };
        var accountExcludedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Account.Id),
            nameof(Account.AccountId),
            nameof(Account.Version),
            nameof(Account.DefaultCurrencySymbol),
            nameof(Account.Name),
            nameof(Account.Slug),
            nameof(Account.Locale),
            nameof(Account.DefaultCurrencyCode),
        };
        ReferenceDataComparison.AssertExactMappedPropertyPartition(
            accountEntityType, accountComparedProperties, accountExcludedProperties);
        Assert.Equal(10, accountComparedProperties.Count);
        Assert.Equal(8, accountExcludedProperties.Count);

        var actualAccount = Assert.Single(await db.Accounts.IgnoreQueryFilters()
            .Where(account => account.Id == SeedDefaults.AccountId)
            .ToListAsync());
        var expectedAccount = Account.Create(
            SeedDefaults.AccountId,
            "Default Farm",
            "default-farm",
            "UTC",
            "USD",
            "en-US");
        ReferenceDataComparison.AssertMappedPropertiesEqualByKey(
            accountEntityType,
            [actualAccount],
            [expectedAccount],
            account => account.Id,
            accountExcludedProperties);

        Assert.NotNull(db.Model.FindEntityType(typeof(EggGrade)));
        var gradeEntityType = db.Model.FindEntityType(typeof(EggGrade))!;
        var actualGrades = await db.EggGrades.IgnoreQueryFilters()
            .Where(grade => grade.AccountId == SeedDefaults.AccountId && grade.FarmId == SeedDefaults.FarmId)
            .ToListAsync();
        ReferenceDataComparison.AssertMappedPropertiesEqualByKey(
            gradeEntityType,
            actualGrades,
            EggGrade.Defaults(SeedDefaults.AccountId, SeedDefaults.FarmId),
            grade => grade.Name,
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(EggGrade.Id), nameof(EggGrade.AccountId), nameof(EggGrade.Version),
            });

        Assert.NotNull(db.Model.FindEntityType(typeof(EggUnitConversion)));
        var conversionEntityType = db.Model.FindEntityType(typeof(EggUnitConversion))!;
        var actualConversions = await db.EggUnitConversions.IgnoreQueryFilters()
            .Where(conversion => conversion.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        ReferenceDataComparison.AssertMappedPropertiesEqualByKey(
            conversionEntityType,
            actualConversions,
            EggUnitConversion.Defaults(SeedDefaults.AccountId),
            conversion => conversion.UnitCode,
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(EggUnitConversion.Id),
                nameof(EggUnitConversion.AccountId),
                nameof(EggUnitConversion.Version),
            });
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

    // #396 — the product decision ("Cracked and Dirty are saleable on a fresh
    // install, and each is bound to its counter") lives entirely in the seed
    // SQL, where nothing else would notice it being reverted: the sibling test
    // above counts ten grades and would still pass with every flag wrong.
    [Fact]
    public async Task MigratingAVirginDatabase_MakesTheTwoConditionGradesSaleableAndBound()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        await db.Database.MigrateAsync();

        var grades = await db.EggGrades.IgnoreQueryFilters()
            .Where(g => g.AccountId == SeedDefaults.AccountId && g.FarmId == SeedDefaults.FarmId)
            .ToListAsync();

        var cracked = Assert.Single(grades, g => g.DailyEntryKind == DailyEntryKind.Cracked);
        var dirty = Assert.Single(grades, g => g.DailyEntryKind == DailyEntryKind.Dirty);

        Assert.Equal("Cracked", cracked.Name);
        Assert.Equal("Dirty", dirty.Name);
        Assert.True(cracked.IsSaleable, "Cracked must be saleable on a fresh install (#396).");
        Assert.True(dirty.IsSaleable, "Dirty must be saleable on a fresh install (#396).");

        // Discarded is always a loss, so it must NOT be bound to a counter —
        // binding it would make discarded eggs resolvable to stock.
        Assert.Equal(
            DailyEntryKind.Manual,
            Assert.Single(grades, g => g.Name == "Discarded").DailyEntryKind);
        Assert.False(Assert.Single(grades, g => g.Name == "Discarded").IsSaleable);

        // Every other seeded grade is hand-graded.
        Assert.Equal(8, grades.Count(g => g.DailyEntryKind == DailyEntryKind.Manual));
    }

    // The partial unique index, proved the same way as the expression indexes
    // above — by the behaviour it exists to produce, not by its name. A name
    // check passes against an index created without its WHERE clause, which
    // would instead forbid a farm from having more than one Manual grade.
    [Fact]
    public async Task MigratingAVirginDatabase_AllowsManyManualGradesButOnlyOneOfEachCondition()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        await db.Database.MigrateAsync();

        // A second Cracked for the same farm is refused.
        var secondCracked = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync($"""
            INSERT INTO "EggGrades" ("Id", "AccountId", "FarmId", "Name", "GradeType", "SortOrder", "IsSaleable", "DailyEntryKind", "Active", "Version")
            VALUES ({Guid.NewGuid()}, {SeedDefaults.AccountId}, {SeedDefaults.FarmId}, 'Cracked 2', 'Quality', 98, TRUE, 'Cracked', TRUE, 0)
            """));

        Assert.NotNull(secondCracked);
        Assert.Contains(
            "IX_EggGrades_AccountId_FarmId_DailyEntryKind",
            secondCracked.ToString(),
            StringComparison.Ordinal);

        // ...but an eleventh Manual grade is fine. This is the half a filterless
        // index would break, and it is why the column default must stay 'Manual'.
        var anotherManual = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync($"""
            INSERT INTO "EggGrades" ("Id", "AccountId", "FarmId", "Name", "GradeType", "SortOrder", "IsSaleable", "DailyEntryKind", "Active", "Version")
            VALUES ({Guid.NewGuid()}, {SeedDefaults.AccountId}, {SeedDefaults.FarmId}, 'Peewee', 'Size', 97, TRUE, 'Manual', TRUE, 0)
            """));

        Assert.Null(anotherManual);
    }
}
