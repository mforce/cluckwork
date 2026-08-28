namespace Cluckwork.Api.IntegrationTests;

using System.Text.RegularExpressions;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

// #531/#407 — the AddAccountSlug backfill. The migration adds Slug nullable,
// backfills a deterministic per-row value, tightens to NOT NULL, then adds a
// unique index. This drives EF's migrator directly against a throwaway Postgres
// (same shape as FarmBannerMigrationDowngradeTests / BaseReferenceDataMigrationTests),
// because the interesting behaviour is the pre-existing-rows path, which a
// normal WebApplicationFactory boot never reproduces.
public sealed class AccountSlugMigrationTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    // The migration immediately before AddAccountSlug. Migrating here leaves the
    // Accounts table WITHOUT a Slug column but WITH the default account
    // (InitialCreate's raw-SQL row), so the backfill runs against a real
    // pre-existing row. Referenced by NAME, not timestamped id, so the test does
    // not couple to whatever timestamp `ef migrations add` happened to mint.
    private const string PreviousMigrationId = "AddUserStepUpLogoutEpoch";
    private const string AddAccountSlugMigrationId = "AddAccountSlug";

    private const string DefaultAccountId = "0000000a-0000-0000-0000-000000000001";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext(), new FlockScope());
    }

    private static Task<string> SlugOfAsync(AppDbContext db, string accountId)
    {
        // Assigned to a plain string first: passing an interpolated literal
        // straight to a *Raw method trips the EF1002 injection analyzer (a
        // build error here). These are test-controlled literal ids, not input.
        string sql = $"SELECT \"Slug\" AS \"Value\" FROM \"Accounts\" WHERE \"Id\" = '{accountId}'::uuid";
        return db.Database.SqlQueryRaw<string>(sql).FirstAsync();
    }

    [Fact]
    public async Task MigratedDatabase_BackfillsTheDefaultAccountToDefaultFarm()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        await db.Database.MigrateAsync();

        Assert.Equal("default-farm", await SlugOfAsync(db, DefaultAccountId));
    }

    [Fact]
    public async Task Backfill_GivesAPreExistingSecondAccount_ADistinctValidSlug()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigrationId);

        // A second account existed BEFORE AddAccountSlug (a non-production
        // database already carries SimulationDataSeeder.SecondAccountId). Copy
        // the default row wholesale — SELECT * so this stays drift-proof as
        // columns change — and give it a fresh id. Runs as one batched command
        // so the TEMP table is visible across the statements.
        var secondId = "0000000a-0000-0000-0000-000000000002";
        string insertSecond =
            $"""
            CREATE TEMP TABLE _acct_copy AS SELECT * FROM "Accounts" WHERE "Id" = '{DefaultAccountId}'::uuid;
            UPDATE _acct_copy SET "Id" = '{secondId}'::uuid, "AccountId" = '{secondId}'::uuid, "Name" = 'Pre-Existing Second Farm';
            INSERT INTO "Accounts" SELECT * FROM _acct_copy;
            DROP TABLE _acct_copy;
            """;
        await db.Database.ExecuteSqlRawAsync(insertSecond);

        await migrator.MigrateAsync(AddAccountSlugMigrationId);

        var defaultSlug = await SlugOfAsync(db, DefaultAccountId);
        var secondSlug = await SlugOfAsync(db, secondId);

        Assert.Equal("default-farm", defaultSlug);
        Assert.NotEqual(defaultSlug, secondSlug);
        Assert.Matches("^farm-[0-9a-f]{12}$", secondSlug);
    }

    [Fact]
    public async Task UniqueIndex_RejectsADuplicateSlug()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        // A fresh row carrying the default account's slug must be refused by
        // IX_Accounts_Slug (the migration's own uniqueness assertion).
        var duplicateId = "0000000a-0000-0000-0000-0000000000ff";
        string insertDuplicate =
            $"""
            CREATE TEMP TABLE _dup_copy AS SELECT * FROM "Accounts" WHERE "Id" = '{DefaultAccountId}'::uuid;
            UPDATE _dup_copy SET "Id" = '{duplicateId}'::uuid, "AccountId" = '{duplicateId}'::uuid, "Slug" = 'default-farm';
            INSERT INTO "Accounts" SELECT * FROM _dup_copy;
            DROP TABLE _dup_copy;
            """;
        var conflict = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(insertDuplicate));

        var postgresException = Assert.IsType<PostgresException>(conflict);
        Assert.Equal("23505", postgresException.SqlState); // unique_violation
    }
}
