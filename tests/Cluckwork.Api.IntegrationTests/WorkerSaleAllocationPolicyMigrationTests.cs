namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

// #612 — the AddWorkerSaleAllocationPolicy migration. Same throwaway-Postgres,
// no-WebApplicationFactory pattern as FarmBannerMigrationDowngradeTests: drives
// EF's migrator directly so the default/backfill and the downgrade are
// verified against a real database, not assumed from the C# source.
public sealed class WorkerSaleAllocationPolicyMigrationTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private const string PreviousMigrationId = "20260819202301_RequireUserIdentityColumns";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext(), new FlockScope());
    }

    [Fact]
    public async Task MigratingUp_BackfillsTheDefaultAccountToAssignedFlocksOnly()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        // InitialCreate's raw-SQL seed inserts the default account (#283)
        // BEFORE this migration ever runs — the row this migration's ADD
        // COLUMN ... DEFAULT clause has to backfill.
        await db.Database.MigrateAsync();

        var value = await db.Database
            .SqlQueryRaw<string>(
                "SELECT \"WorkerSaleAllocationPolicy\" AS \"Value\" FROM \"Accounts\" LIMIT 1")
            .FirstAsync();
        Assert.Equal("AssignedFlocksOnly", value);
    }

    [Fact]
    public async Task DowngradingPastAddWorkerSaleAllocationPolicy_DropsTheColumn_AndUpgradesCleanlyAgain()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigrationId);

        var columnCount = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int AS "Value" FROM information_schema.columns
                WHERE table_name = 'Accounts' AND column_name = 'WorkerSaleAllocationPolicy'
                """)
            .FirstAsync();
        Assert.Equal(0, columnCount);

        // Round-trips back up without error and re-backfills.
        await migrator.MigrateAsync();
        var value = await db.Database
            .SqlQueryRaw<string>(
                "SELECT \"WorkerSaleAllocationPolicy\" AS \"Value\" FROM \"Accounts\" LIMIT 1")
            .FirstAsync();
        Assert.Equal("AssignedFlocksOnly", value);
    }
}
