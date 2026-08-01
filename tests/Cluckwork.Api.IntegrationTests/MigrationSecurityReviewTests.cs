namespace Cluckwork.Api.IntegrationTests;

using System.Reflection;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

// #283 — "the `SecurityReviewed`-style assertion the issue calls for": no EF
// migration may EVER embed a credential (or anything credential-shaped). Part
// 1 moves roles/egg grades/the default account into the schema via
// InsertData; this is what proves that move never smuggled a user row (and
// therefore a password hash) in with it — now, or in any migration added
// later. No Docker/Postgres needed: Migration.UpOperations is computed purely
// by running each migration's Up(MigrationBuilder) in memory, never touching
// a database.
public sealed class MigrationSecurityReviewTests
{
    // Identity's own credential-shaped columns (AspNetUsers). A future
    // migration that inserts a row carrying any of these — in ANY table, not
    // just AspNetUsers — is exactly the mistake this test exists to catch.
    private static readonly string[] CredentialShapedColumns =
        ["PasswordHash", "SecurityStamp", "TwoFactorRecoveryCode"];

    private static IReadOnlyList<Migration> AllMigrations() =>
        typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (Migration)Activator.CreateInstance(t)!)
            .ToList();

    private static IEnumerable<InsertDataOperation> AllInsertDataOperations() =>
        AllMigrations().SelectMany(m => m.UpOperations).OfType<InsertDataOperation>();

    [Fact]
    public void NoMigration_EverInsertsIntoTheUsersTable()
    {
        var offenders = AllInsertDataOperations()
            .Where(op => string.Equals(op.Table, "AspNetUsers", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A migration inserts directly into AspNetUsers — that bakes a user (and its password hash) " +
            "into the repo, shipping every deployment the same publicly-known credential. The first admin " +
            "must only ever be created at runtime by the `bootstrap-admin` command.");
    }

    [Fact]
    public void NoMigration_EverInsertsACredentialShapedColumn_IntoAnyTable()
    {
        var offenders = AllInsertDataOperations()
            .Where(op => op.Columns.Any(c => CredentialShapedColumns.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .Select(op => op.Table)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A migration's InsertData includes a credential-shaped column " +
            $"({string.Join(", ", CredentialShapedColumns)}) on table(s): {string.Join(", ", offenders)}.");
    }

    // The positive assertion mirroring the two negative ones above: this
    // migration DOES seed the static reference data Part 1 promises, and
    // nothing more than that data.
    [Fact]
    public void AddBaseReferenceDataMigration_SeedsExactlyTheStaticReferenceRows()
    {
        var migration = AllMigrations()
            .Single(m => m.GetType().Name == "AddBaseReferenceDataAndMustChangePassword");
        var inserts = migration.UpOperations.OfType<InsertDataOperation>().ToList();

        var accountInsert = Assert.Single(inserts, op => op.Table == "Accounts");
        Assert.Equal(1, accountInsert.Values.GetLength(0));
        var accountIdIndex = Array.IndexOf(accountInsert.Columns, "Id");
        Assert.Equal(SeedDefaults.AccountId, accountInsert.Values[0, accountIdIndex]);

        var roleInsert = Assert.Single(inserts, op => op.Table == "AspNetRoles");
        Assert.Equal(Roles.Assignable.Count, roleInsert.Values.GetLength(0));
        var roleNameIndex = Array.IndexOf(roleInsert.Columns, "Name");
        var seededRoleNames = Enumerable.Range(0, roleInsert.Values.GetLength(0))
            .Select(i => (string)roleInsert.Values[i, roleNameIndex]!)
            .ToList();
        Assert.Equal(Roles.Assignable.OrderBy(n => n), seededRoleNames.OrderBy(n => n));

        var gradeInsert = Assert.Single(inserts, op => op.Table == "EggGrades");
        Assert.Equal(10, gradeInsert.Values.GetLength(0));

        var unitInsert = Assert.Single(inserts, op => op.Table == "EggUnitConversions");
        Assert.Equal(6, unitInsert.Values.GetLength(0));

        // Nothing else — the whole point of "static reference data, no
        // runtime seeder" is that this migration touches exactly these four
        // tables via InsertData.
        Assert.Equal(4, inserts.Count);
    }
}
