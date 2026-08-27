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

// #532 — THE GUARANTEE that ApplicationUserIndexModelTests points to: it runs
// EF's migrator to completion against a throwaway Postgres (same shape as
// AccountSlugMigrationTests) and proves the database itself — not the
// application, not AccountScopedUserValidator — enforces the account-scoped
// identity contract:
//   * the same email is admissible in two accounts,
//   * a duplicate within one account is a 23505 from EITHER index (both
//     directions, because every user has UserName == Email, so a
//     single-shape test would still pass with either index missing),
//   * an AccountId with no Accounts row is a 23503 from the foreign key.
//
// The inserts are raw SQL, never UserManager: Identity would normalise and the
// validator would reject the duplicate first, so the test would pass with the
// unique index deleted from the migration and prove nothing.
public sealed class AccountScopedIdentityMigrationTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private const string DefaultAccountId = "0000000a-0000-0000-0000-000000000001";
    private const string SecondAccountId = "0000000a-0000-0000-0000-000000000002";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext(), new FlockScope());
    }

    // Test-controlled literal ids/emails, not input.
    private static string InsertUserSql(
        string userId, string accountId, string email, string normalisedEmail,
        string userName, string normalisedUserName)
    {
        return $"""
            INSERT INTO "AspNetUsers" (
                "Id", "AccountId", "UserName", "NormalizedUserName",
                "Email", "NormalizedEmail",
                "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "MustChangePassword",
                "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled",
                "AccessFailedCount")
            VALUES (
                '{userId}'::uuid, '{accountId}'::uuid,
                '{userName}', '{normalisedUserName}',
                '{email}', '{normalisedEmail}',
                'hash-placeholder', 'stamp-placeholder', 'stamp-placeholder', false,
                false, false, false, true, 0);
            """;
    }

    // A row with exactly ONE of the four identity columns NULL — for the NOT
    // NULL column guards. Each column is pinned by its own case: the pre-#532
    // helper nulled BOTH Email and NormalizedEmail while its name said only
    // Email, so deleting either column's AlterColumn still yielded 23502 from
    // the sibling and neither operation was independently provable (round-3
    // review). Test-controlled literal id, not input.
    private static string InsertUserWithOneNullIdentityColumnSql(
        string userId, string accountId, string nullColumn)
    {
        var emailValue = nullColumn switch
        {
            "Email" => "NULL",
            _ => "'nullemail@example.com'",
        };
        var normalizedEmailValue = nullColumn switch
        {
            "NormalizedEmail" => "NULL",
            _ => "'NULLEMAIL@EXAMPLE.COM'",
        };
        var userNameValue = nullColumn switch
        {
            "UserName" => "NULL",
            _ => "'nullemail@example.com'",
        };
        var normalizedUserNameValue = nullColumn switch
        {
            "NormalizedUserName" => "NULL",
            _ => "'NULLEMAIL@EXAMPLE.COM'",
        };

        return $"""
            INSERT INTO "AspNetUsers" (
                "Id", "AccountId", "UserName", "NormalizedUserName",
                "Email", "NormalizedEmail",
                "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "MustChangePassword",
                "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled",
                "AccessFailedCount")
            VALUES (
                '{userId}'::uuid, '{accountId}'::uuid,
                {userNameValue}, {normalizedUserNameValue},
                {emailValue}, {normalizedEmailValue},
                'hash-placeholder', 'stamp-placeholder', 'stamp-placeholder', false,
                false, false, false, true, 0);
            """;
    }

    private static Task InsertAccountAsync(AppDbContext db, string accountId)
    {
        // Copy the default row wholesale (SELECT * keeps this drift-proof as
        // Accounts columns change) and give it a fresh id, like
        // AccountSlugMigrationTests does. Runs as one batched command so the
        // TEMP table is visible across the statements.
        // The copy must also get a fresh unique slug (#531's IX_Accounts_Slug
        // would 23505 otherwise), and a unique Name.
        string sql = $"""
            CREATE TEMP TABLE _acct_copy AS SELECT * FROM "Accounts" WHERE "Id" = '{DefaultAccountId}'::uuid;
            UPDATE _acct_copy SET "Id" = '{accountId}'::uuid, "AccountId" = '{accountId}'::uuid,
                "Name" = 'Second Farm', "Slug" = 'second-farm';
            INSERT INTO "Accounts" SELECT * FROM _acct_copy;
            DROP TABLE _acct_copy;
            """;
        return db.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task MigratedDatabase_AcceptsTheSameEmail_InTwoAccounts()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        await InsertAccountAsync(db, SecondAccountId);

        // Same email (and, per the app's invariant, same NormalizedUserName)
        // in two different accounts — the exact scenario #532 exists to make
        // legal. A globally unique index would 23505 the second insert.
        await db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000101", DefaultAccountId,
            "shared@example.com", "SHARED@EXAMPLE.COM",
            "shared@example.com", "SHARED@EXAMPLE.COM"));
        await db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000102", SecondAccountId,
            "shared@example.com", "SHARED@EXAMPLE.COM",
            "shared@example.com", "SHARED@EXAMPLE.COM"));
    }

    [Fact]
    public async Task MigratedDatabase_TheEmailIndex_RejectsADuplicateWithinOneAccount()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000101", DefaultAccountId,
            "shared@example.com", "SHARED@EXAMPLE.COM",
            "shared@example.com", "SHARED@EXAMPLE.COM"));

        // Same AccountId + NormalizedEmail, a DIFFERENT NormalizedUserName:
        // only the composite EmailIndex can catch this (the UserNameIndex
        // sees a distinct second column).
        var conflict = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000103", DefaultAccountId,
            "other@example.com", "SHARED@EXAMPLE.COM",
            "other@example.com", "OTHER@EXAMPLE.COM")));

        var postgresException = Assert.IsType<PostgresException>(conflict);
        Assert.Equal("23505", postgresException.SqlState); // unique_violation
    }

    [Fact]
    public async Task MigratedDatabase_TheUserNameIndex_RejectsADuplicateWithinOneAccount()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000101", DefaultAccountId,
            "shared@example.com", "SHARED@EXAMPLE.COM",
            "shared@example.com", "SHARED@EXAMPLE.COM"));

        // Same AccountId + NormalizedUserName, a DIFFERENT NormalizedEmail:
        // only the composite UserNameIndex can catch this.
        var conflict = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000104", DefaultAccountId,
            "other@example.com", "OTHER@EXAMPLE.COM",
            "other@example.com", "SHARED@EXAMPLE.COM")));

        var postgresException = Assert.IsType<PostgresException>(conflict);
        Assert.Equal("23505", postgresException.SqlState); // unique_violation
    }

    [Fact]
    public async Task MigratedDatabase_TheForeignKey_RejectsAnOrphanAccountId()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        // An AccountId matching no Accounts row. Without
        // FK_AspNetUsers_Accounts_AccountId this insert is simply ACCEPTED, so
        // the rejection is the constraint doing its job.
        var conflict = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000105",
            "0000000a-0000-0000-0000-000000000fff",
            "orphan@example.com", "ORPHAN@EXAMPLE.COM",
            "orphan@example.com", "ORPHAN@EXAMPLE.COM")));

        Assert.NotNull(conflict);
        var postgresException = Assert.IsType<PostgresException>(conflict);
        // 23503 = foreign_key_violation, and ONLY that. Accepting 23502
        // (not_null_violation) as well would make this test pass with the
        // foreign key absent, which is the single thing it exists to prove.
        Assert.Equal("23503", postgresException.SqlState);
    }

    // One case per identity column so each of the four AlterColumn operations
    // in RequireUserIdentityColumns is independently pinned: null exactly one
    // column and only the NOT NULL constraint on THAT column can refuse the row.
    [Theory]
    [InlineData("Email", "0000000a-0000-0000-0000-000000000106")]
    [InlineData("NormalizedEmail", "0000000a-0000-0000-0000-00000000010a")]
    [InlineData("UserName", "0000000a-0000-0000-0000-00000000010b")]
    [InlineData("NormalizedUserName", "0000000a-0000-0000-0000-00000000010c")]
    public async Task MigratedDatabase_RejectsAnInsertWithANullIdentityColumn(string nullColumn, string userId)
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        // Raw SQL, never UserManager: Identity would normalise and fill the
        // column before the database ever saw a NULL.
        var conflict = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(
            InsertUserWithOneNullIdentityColumnSql(userId, DefaultAccountId, nullColumn)));

        // ONLY 23502 (not_null_violation) is accepted — the Postgres signal
        // that a plain SQL NULL reached the parameter against a NOT NULL
        // column. A set containing 23505 would let a UNIQUE constraint (not
        // the NOT NULL one) be the thing refusing the row, which is not what
        // this proves.
        var postgresException = Assert.IsAssignableFrom<NpgsqlException>(conflict);
        // 23502 = not_null_violation, the ONLY value accepted.
        //
        // (Do not "help" a 22P02 failure by widening the accepted set to cover
        // it: 22P02 here means invalid_input_syntax on a column VALUE, which is
        // a test-data defect in this helper, not a second shape of the
        // constraint being enforced.)
        Assert.Equal("23502", postgresException.SqlState);
    }

    [Fact]
    public async Task TheIdentityIndexMigration_RefusesToRun_OverAnOrphanUserId()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        // Migrations by NAME, not timestamped id: AccountSlugMigrationTests
        // explains why — the timestamp is whatever `ef migrations add` mints.
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("AddAccountSlug");

        // No FK on AspNetUsers.AccountId at this point, so an orphan is simply
        // insertable — exactly the row the pre-check exists to refuse.
        await db.Database.ExecuteSqlRawAsync(InsertUserSql(
            "0000000a-0000-0000-0000-000000000107",
            "0000000a-0000-0000-0000-000000000fff",
            "orphan@example.com", "ORPHAN@EXAMPLE.COM",
            "orphan@example.com", "ORPHAN@EXAMPLE.COM"));

        var conflict = await Record.ExceptionAsync(() => migrator.MigrateAsync());

        var postgresException = Assert.IsType<PostgresException>(conflict);
        Assert.Contains(
            "reference an account that does not exist", postgresException.Message);
    }

    [Fact]
    public async Task TheRequireColumnsMigration_RefusesToRun_OverANullIdentityColumn()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());

        // The columns are still nullable at this point, so a NULL is insertable
        // — exactly the row the pre-check exists to refuse.
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("AccountScopedIdentityIndexes");

        await db.Database.ExecuteSqlRawAsync(InsertUserWithOneNullIdentityColumnSql(
            "0000000a-0000-0000-0000-000000000108", DefaultAccountId, "Email"));

        var conflict = await Record.ExceptionAsync(() => migrator.MigrateAsync());

        var postgresException = Assert.IsType<PostgresException>(conflict);
        Assert.Contains(
            "have a NULL Email, NormalizedEmail, UserName or NormalizedUserName", postgresException.Message);
    }
}
