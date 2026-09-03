namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

// #670 — the migration that adds AspNetUserRoles.AccountId runs against a
// database that ALREADY holds role rows (every real one). EF's generated
// operations alone add the column with DEFAULT '0000…' and then create the
// composite FK, which fails on any pre-existing row; the two hand-inserted SQL
// steps (backfill from AspNetUsers, then DROP DEFAULT) are what make it run.
// The suite's own database is empty when it migrates, so it cannot see either
// step missing — this test can: migrate by NAME to the migration BEFORE this
// slice's, insert a user and a role row by raw SQL, migrate forward, and read
// the database. Shape: AccountScopedIdentityMigrationTests (#532).
public sealed class UserRoleAccountIdMigrationTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    // Base reference data the migrations themselves seed (#283): the default
    // account and the Owner role.
    private const string DefaultAccountId = "0000000a-0000-0000-0000-000000000001";
    private const string OwnerRoleId = "0000000c-0000-0000-0000-000000000001";
    private const string UserId = "0000000a-0000-0000-0000-000000000670";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext(), new FlockScope());
    }

    // Raw SQL, never UserManager: at this migration point the table has no
    // AccountId column for the interceptor to stamp, and the point of the
    // test is a row that pre-dates the column. Test-controlled literal ids.
    private const string InsertUserSql = $"""
        INSERT INTO "AspNetUsers" (
            "Id", "AccountId", "UserName", "NormalizedUserName",
            "Email", "NormalizedEmail",
            "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "MustChangePassword",
            "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled",
            "AccessFailedCount")
        VALUES (
            '{UserId}'::uuid, '{DefaultAccountId}'::uuid,
            'pre670@example.com', 'PRE670@EXAMPLE.COM',
            'pre670@example.com', 'PRE670@EXAMPLE.COM',
            'hash-placeholder', 'stamp-placeholder', 'stamp-placeholder', false,
            false, false, false, true, 0);
        """;

    private const string InsertUserRoleSql = $"""
        INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
        VALUES ('{UserId}'::uuid, '{OwnerRoleId}'::uuid);
        """;

    [Fact]
    public async Task TheUserRolesMigration_BackfillsAccountIdFromTheUser_AndLeavesNoColumnDefault()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        // Migrations by NAME, not timestamped id (AccountSlugMigrationTests
        // explains why). AccountIdConcurrencyToken (#562) is the migration
        // immediately before this slice's.
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("AccountIdConcurrencyToken");

        await db.Database.ExecuteSqlRawAsync(InsertUserSql);
        await db.Database.ExecuteSqlRawAsync(InsertUserRoleSql);

        var failure = await Record.ExceptionAsync(() => migrator.MigrateAsync());
        Assert.True(failure is null,
            "migrating forward over a pre-existing role row failed — the backfill did not run before the FK was created: " +
            (failure is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : failure?.ToString()));

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();

        await using (var columns = new NpgsqlCommand(
            """
            SELECT count(*), max(column_default)
            FROM information_schema.columns
            WHERE table_name = 'AspNetUserRoles' AND column_name = 'AccountId'
            """, connection))
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetInt64(0) == 1, "AspNetUserRoles has no AccountId column (#670 migration missing)");
            var columnDefault = reader.IsDBNull(1) ? null : reader.GetString(1);
            Assert.True(columnDefault is null,
                $"AspNetUserRoles.AccountId still carries a column DEFAULT ({columnDefault}) — the DROP DEFAULT step did not run");
        }

        await using var backfilled = new NpgsqlCommand(
            $"""
            SELECT "AccountId" FROM "AspNetUserRoles"
            WHERE "UserId" = '{UserId}'::uuid AND "RoleId" = '{OwnerRoleId}'::uuid
            """, connection);
        var accountId = (Guid?)await backfilled.ExecuteScalarAsync();

        Assert.Equal(Guid.Parse(DefaultAccountId), accountId);
    }
}
