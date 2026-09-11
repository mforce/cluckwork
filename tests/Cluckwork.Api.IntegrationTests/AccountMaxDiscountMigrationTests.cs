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

    // #673's precedent: the range fails closed in BOTH layers. The application
    // check in Account.UpdateSettings guards the write path; this guards the
    // row. It is what makes Account.MaxDiscount's FromBasisPoints throw
    // unreachable — that getter runs on the role-agnostic GET /account, so a
    // single out-of-range row would 500 every page load on the farm, including
    // the Settings screen that would correct it, leaving raw SQL as the only
    // recovery. AGENTS.md records under #732 that raw UPDATEs against this
    // table do happen.
    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task TheDatabaseRefusesABasisPointValueOutsideTheRange(int basisPoints)
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        // Raw SQL on purpose: the aggregate refuses this already, so going
        // through it would prove nothing about the column.
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Accounts" SET "MaxDiscountBasisPoints" = {basisPoints}"""));

        Assert.Equal("23514", refused.SqlState); // check_violation
        Assert.Equal("CK_Accounts_MaxDiscountBasisPoints", refused.ConstraintName);
    }

    // NULL is the legal "no ceiling" default and 0 is the legal "give nothing
    // away" setting, so the constraint must admit both — a naive
    // BETWEEN 0 AND 10000 with no IS NULL arm would reject every existing farm.
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(10_000)]
    public async Task TheDatabaseAcceptsEveryValueTheApplicationCanWrite(int? basisPoints)
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Accounts" SET "MaxDiscountBasisPoints" = {basisPoints}""");

        Assert.Equal(1, rows);
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
