using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

// #1276 (G1.2) — the AddTwoFactorStorage migration over a database that already
// holds a token row, the shape of UserRoleAccountIdMigrationTests (#670): migrate
// by NAME to the migration before it, insert a user and a token row by raw SQL,
// migrate forward, and read the database. It also pins the defaults of the
// columns the migration adds, which keep raw-SQL user fixtures inserting.
public sealed class UserTokenAccountIdMigrationTests
{
    private const string DefaultAccountId = "0000000a-0000-0000-0000-000000000001";
    private const string UserId = "0000000a-0000-0000-0000-000000001276";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext(), new FlockScope());
    }

    private const string InsertUserSql = $"""
        INSERT INTO "AspNetUsers" (
            "Id", "AccountId", "UserName", "NormalizedUserName",
            "Email", "NormalizedEmail",
            "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "MustChangePassword",
            "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled",
            "AccessFailedCount")
        VALUES (
            '{UserId}'::uuid, '{DefaultAccountId}'::uuid,
            'pre1276@example.com', 'PRE1276@EXAMPLE.COM',
            'pre1276@example.com', 'PRE1276@EXAMPLE.COM',
            'hash-placeholder', 'stamp-placeholder', 'stamp-placeholder', false,
            false, false, false, true, 0);
        """;

    private const string InsertUserTokenSql = $"""
        INSERT INTO "AspNetUserTokens" ("UserId", "LoginProvider", "Name", "Value")
        VALUES ('{UserId}'::uuid, 'Cluckwork', 'Pre1276', 'v');
        """;

    [Fact]
    public async Task TheTwoFactorStorageMigration_BackfillsTokenAccountId_AndGivesEachNewColumnItsDefault()
    {
        await using var postgres = new SharedPostgresDatabase();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("AddAccountAllowConnectedApps");

        await db.Database.ExecuteSqlRawAsync(InsertUserSql);
        await db.Database.ExecuteSqlRawAsync(InsertUserTokenSql);

        var failure = await Record.ExceptionAsync(() => migrator.MigrateAsync());
        Assert.True(failure is null,
            "migrating forward over a pre-existing token row failed — the backfill did not run before the FK was created: " +
            (failure is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : failure?.ToString()));

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();

        var columns = new Dictionary<string, (bool Nullable, string? Default)>();
        await using (var read = new NpgsqlCommand(
            """
            SELECT table_name || '.' || column_name, is_nullable = 'YES', column_default
            FROM information_schema.columns
            WHERE (table_name = 'AspNetUserTokens' AND column_name = 'AccountId')
               OR (table_name = 'AspNetUsers' AND column_name LIKE 'SecondFactor%')
               OR (table_name = 'refresh_tokens' AND column_name = 'SignedInAt')
            """, connection))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                columns[reader.GetString(0)] = (reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        var expected = new Dictionary<string, (bool Nullable, string? Default)>
        {
            ["AspNetUserTokens.AccountId"] = (false, null),
            ["AspNetUsers.SecondFactorWatermark"] = (false, "0"),
            ["AspNetUsers.SecondFactorSignIns"] = (false, "0"),
            ["AspNetUsers.SecondFactorFailures"] = (false, "0"),
            ["AspNetUsers.SecondFactorFailuresSince"] = (true, null),
            ["AspNetUsers.SecondFactorRequiredSince"] = (true, null),
            ["refresh_tokens.SignedInAt"] = (true, null),
        };
        Assert.Equal(expected.OrderBy(c => c.Key), columns.OrderBy(c => c.Key));

        await using var backfilled = new NpgsqlCommand(
            $"""
            SELECT "AccountId" FROM "AspNetUserTokens"
            WHERE "UserId" = '{UserId}'::uuid AND "LoginProvider" = 'Cluckwork' AND "Name" = 'Pre1276'
            """, connection);
        Assert.Equal(Guid.Parse(DefaultAccountId), (Guid?)await backfilled.ExecuteScalarAsync());
    }
}
