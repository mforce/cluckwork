namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

// #727 — the AddAccountMaxDiscountBasisPoints migration, same
// throwaway-Postgres, no-WebApplicationFactory pattern as
// WorkerSaleAllocationPolicyMigrationTests. The assertion is the opposite of
// that one's: this column must land NULL, because "no ceiling" is the legal
// default and a defaultValue of 0 would mean the opposite — give nothing away.
public sealed class AccountMaxDiscountMigrationTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private const string PreviousMigrationId = "20260910222552_AddSalesOrderDiscountReason";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext(), new FlockScope());
    }

    private static Task<int> NullCeilingCountAsync(AppDbContext db) =>
        db.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int AS "Value" FROM "Accounts"
                WHERE "MaxDiscountBasisPoints" IS NULL
                """)
            .FirstAsync();

    [Fact]
    public async Task MigratingUp_LeavesTheDefaultAccountWithNoCeiling()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        // InitialCreate's raw-SQL seed inserts the default account (#283) before
        // this migration runs, so this row is exactly the one a defaultValue
        // would have backfilled.
        await db.Database.MigrateAsync();

        Assert.Equal(1, await NullCeilingCountAsync(db));
    }

    [Fact]
    public async Task DowngradingPastAddAccountMaxDiscountBasisPoints_DropsTheColumn_AndUpgradesCleanlyAgain()
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
                WHERE table_name = 'Accounts' AND column_name = 'MaxDiscountBasisPoints'
                """)
            .FirstAsync();
        Assert.Equal(0, columnCount);

        await migrator.MigrateAsync();
        Assert.Equal(1, await NullCeilingCountAsync(db));
    }
}
