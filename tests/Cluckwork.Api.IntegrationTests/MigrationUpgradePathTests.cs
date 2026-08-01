namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

// #283, PR #339 review — the CENTRAL upgrade-path guarantee:
// AddBaseReferenceDataAndMustChangePassword must apply cleanly to a database
// that already ran the OLD runtime DatabaseSeeder (every real deployment,
// every dev DB, every CI/sim DB that ever booted the app before this PR — see
// AGENTS.md's pre-#283 "Migrations + seed" history). The old seeder used a
// FIXED id for the Account but RANDOM ids (Guid.NewGuid()) for every Role,
// EggGrade, and EggUnitConversion row. A first attempt at this migration used
// EF's HasData()/InsertData (keyed by PRIMARY KEY) and broke on exactly this:
// same Account id -> 23505 on PK_Accounts; divergent Role/EggGrade/
// EggUnitConversion ids -> silently violates the NATURAL-key unique indexes
// instead (RoleNameIndex, IX_EggGrades_AccountId_FarmId_LowerName,
// IX_EggUnitConversions_AccountId_UnitCode) — caught empirically here.
//
// No WebApplicationFactory needed: this drives EF's migrator directly
// against a throwaway Testcontainers Postgres (same pattern
// AppDbContextDesignTimeFactory uses to build an AppDbContext outside ASP.NET
// DI), which also means each [Fact] below gets its OWN fully isolated
// container — required here since the two tests apply migrations to
// deliberately different starting states.
public sealed class MigrationUpgradePathTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString);
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext());
    }

    private const string TargetMigrationSuffix = "_AddBaseReferenceDataAndMustChangePassword";

    // The migration immediately BEFORE the one under test, resolved from the
    // assembly's own migration list rather than hardcoded — the coordinator
    // said main has moved and #270/#307 land before this one at merge time,
    // so a literal prior-migration id here would silently stop being "the
    // one right before" after that rebase. Whatever migration actually
    // precedes AddBaseReferenceDataAndMustChangePassword in THIS build is the
    // right target for "migrate to just before it".
    private static string ResolvePreviousMigrationId(AppDbContext db)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var ids = assembly.Migrations.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var targetIndex = ids.FindIndex(id => id.EndsWith(TargetMigrationSuffix, StringComparison.Ordinal));
        Assert.True(targetIndex > 0,
            $"Expected a migration ending in '{TargetMigrationSuffix}' with at least one migration before it.");
        return ids[targetIndex - 1];
    }

    [Fact]
    public async Task UpgradingFromAnOldSeederPopulatedDatabase_Succeeds_AdoptsExistingRows_NoDuplicates()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        var migrator = db.GetService<IMigrator>();

        // 1. Migrate to just short of the migration under test — the schema
        // state every real pre-#283 deployment was on.
        await migrator.MigrateAsync(ResolvePreviousMigrationId(db));

        // 2. Seed rows EXACTLY the way the old runtime DatabaseSeeder did:
        // the Account under the fixed SeedDefaults.AccountId, but Roles/
        // EggGrades/EggUnitConversions each under a fresh Guid.NewGuid() —
        // reproducing the real divergence the migration must tolerate.
        // (Roles: a direct insert with the same Name/NormalizedName/
        // ConcurrencyStamp shape RoleManager.CreateAsync's default
        // UpperInvariantLookupNormalizer produces — the old seeder's actual
        // call — without pulling in a full Identity RoleManager/DI stack
        // just to mint four rows.)
        db.Accounts.Add(Account.Create(SeedDefaults.AccountId, "Default Farm", "UTC", "USD"));
        await db.SaveChangesAsync();

        var preExistingRoleIds = new Dictionary<string, Guid>();
        foreach (var name in Roles.Assignable)
        {
            var role = new ApplicationRole
            {
                Id = Guid.NewGuid(),
                Name = name,
                NormalizedName = name.ToUpperInvariant(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            };
            db.Roles.Add(role);
            preExistingRoleIds[name] = role.Id;
        }
        await db.SaveChangesAsync();

        var gradeSpecs = new (string Name, EggGradeType Type, int SortOrder, bool Saleable)[]
        {
            ("Small", EggGradeType.Size, 0, true),
            ("Medium", EggGradeType.Size, 1, true),
            ("Large", EggGradeType.Size, 2, true),
            ("Jumbo", EggGradeType.Size, 3, true),
            ("Seconds", EggGradeType.Quality, 4, true),
            ("Cracked", EggGradeType.Quality, 5, false),
            ("Dirty", EggGradeType.Quality, 6, false),
            ("Soft Shell", EggGradeType.Quality, 7, false),
            ("Discarded", EggGradeType.Custom, 8, false),
            ("Internal Use", EggGradeType.Custom, 9, false),
        };
        var preExistingGradeIds = new Dictionary<string, Guid>();
        foreach (var (name, type, sortOrder, saleable) in gradeSpecs)
        {
            var grade = EggGrade.Create(
                Guid.NewGuid(), SeedDefaults.AccountId, SeedDefaults.FarmId, name, type, sortOrder, saleable);
            db.EggGrades.Add(grade);
            preExistingGradeIds[name] = grade.Id;
        }
        await db.SaveChangesAsync();

        var conversionDefaults = EggUnitConversion.Defaults(SeedDefaults.AccountId);
        db.EggUnitConversions.AddRange(conversionDefaults);
        await db.SaveChangesAsync();
        var preExistingUnitIds = conversionDefaults.ToDictionary(d => d.UnitCode, d => d.Id);

        // 3. Apply the migration under test — this must NOT throw.
        var upgrade = await Record.ExceptionAsync(() => migrator.MigrateAsync());
        Assert.Null(upgrade);

        // 4. No duplicates: still exactly one account, four roles, ten
        // grades, six unit conversions.
        Assert.Equal(1, await db.Accounts.IgnoreQueryFilters()
            .CountAsync(a => a.Id == SeedDefaults.AccountId));
        Assert.Equal(4, await db.Roles.CountAsync(r => Roles.Assignable.Contains(r.Name)));
        Assert.Equal(10, await db.EggGrades.IgnoreQueryFilters()
            .CountAsync(g => g.AccountId == SeedDefaults.AccountId && g.FarmId == SeedDefaults.FarmId));
        Assert.Equal(6, await db.EggUnitConversions.IgnoreQueryFilters()
            .CountAsync(c => c.AccountId == SeedDefaults.AccountId));

        // 5. The migration ADOPTED the pre-existing rows rather than
        // rewriting them: every id in the database is still the RANDOM one
        // this test minted before migrating, not the migration's fixed
        // literal. This is the load-bearing assertion — a version of the fix
        // that deleted-then-reinserted, or "fixed" the ids in place, would
        // pass the count assertions above but fail these.
        foreach (var (name, expectedId) in preExistingRoleIds)
        {
            var normalized = name.ToUpperInvariant();
            var actual = await db.Roles.Where(r => r.NormalizedName == normalized)
                .Select(r => r.Id).SingleAsync();
            Assert.Equal(expectedId, actual);
        }
        foreach (var (name, expectedId) in preExistingGradeIds)
        {
            var actual = await db.EggGrades.IgnoreQueryFilters()
                .Where(g => g.AccountId == SeedDefaults.AccountId && g.Name == name)
                .Select(g => g.Id).SingleAsync();
            Assert.Equal(expectedId, actual);
        }
        foreach (var (unitCode, expectedId) in preExistingUnitIds)
        {
            var actual = await db.EggUnitConversions.IgnoreQueryFilters()
                .Where(c => c.AccountId == SeedDefaults.AccountId && c.UnitCode == unitCode)
                .Select(c => c.Id).SingleAsync();
            Assert.Equal(expectedId, actual);
        }
    }

    // The companion positive case the reviewer asked for: a database that
    // NEVER ran the old seeder (a real fresh deploy, or any CI/test database
    // spun up after #283) still ends up with exactly one of each row — the
    // WHERE NOT EXISTS guards must not turn into a no-op when there is
    // nothing to guard against. Own (fresh, never-touched) container.
    [Fact]
    public async Task UpgradingAVirginDatabase_StillProducesExactlyOneOfEachRow()
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
}
