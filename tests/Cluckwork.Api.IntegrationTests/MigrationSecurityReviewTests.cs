namespace Cluckwork.Api.IntegrationTests;

using System.Text.RegularExpressions;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

// #283 — "the `SecurityReviewed`-style assertion the issue calls for": no EF
// migration may EVER embed a credential (or anything credential-shaped). Part
// 1 moves roles/egg grades/the default account/default packed-unit
// conversions into the schema via idempotent raw SQL (migrationBuilder.Sql —
// NOT HasData/InsertData, see the InitialCreate migration's own header
// comment for why); this is what proves that move never smuggled a
// user row (and therefore a password hash) in with it — now, or in any
// migration added later, in EITHER form (EF's generated InsertData, or raw
// SQL). No Docker/Postgres needed: Migration.UpOperations is computed purely
// by running each migration's Up(MigrationBuilder) in memory, never touching
// a database.
//
// PR #339 review — the first version of this file only looked at
// InsertDataOperation.Columns against a fixed name allowlist: blind to
// SqlOperation entirely (so a raw-SQL insert sailed through untouched), and
// blind to InsertDataOperation.Values (so a credential under an unexpected
// column NAME would also sail through). Both gaps are closed below: every
// migration's SQL TEXT is scanned for an AspNetUsers insert and for a
// credential-shaped column name, and every VALUE — in both InsertData rows
// and SQL literal text — is checked against the hash SHAPE Identity's
// PasswordHasher actually produces, independent of what column it's under.
public sealed class MigrationSecurityReviewTests
{
    // Identity's own credential-shaped columns (AspNetUsers). A future
    // migration that inserts a row carrying any of these — in ANY table, not
    // just AspNetUsers — is exactly the mistake this test exists to catch.
    private static readonly string[] CredentialShapedColumns =
        ["PasswordHash", "SecurityStamp", "TwoFactorRecoveryCode"];

    // ASP.NET Core Identity's PasswordHasher (V3, the current default) always
    // starts a hashed password with 0x01 followed by iteration-count/format
    // bytes, which base64-encodes to this literal 6-character prefix — see
    // Microsoft.AspNetCore.Cryptography.KeyDerivation's PasswordHasher
    // source. A real hash is also always long (~84 chars for V3); the length
    // floor below is a second, independent signal so this doesn't rely on
    // the marker alone (a future hasher version could change the prefix).
    private const string IdentityHashV3Marker = "AQAAAA";
    private const int MinPlausibleHashLength = 40;
    private static readonly Regex Base64Like = new(@"^[A-Za-z0-9+/=]+$", RegexOptions.Compiled);

    private static IReadOnlyList<Migration> AllMigrations() =>
        typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (Migration)Activator.CreateInstance(t)!)
            .ToList();

    private static IEnumerable<InsertDataOperation> AllInsertDataOperations() =>
        AllMigrations().SelectMany(m => m.UpOperations).OfType<InsertDataOperation>();

    private static IEnumerable<SqlOperation> AllSqlOperations() =>
        AllMigrations().SelectMany(m => m.UpOperations).OfType<SqlOperation>();

    // True for a string that LOOKS like an Identity PBKDF2 hash — the
    // V3 marker prefix, or (independently) just plain long+base64-shaped,
    // since a future format change might drop the marker but a hash is
    // still going to be a long opaque base64 blob. Deliberately column-name
    // agnostic: this is the "regardless of column name" half of the guard.
    private static bool LooksHashShaped(string value) =>
        value.StartsWith(IdentityHashV3Marker, StringComparison.Ordinal)
        || (value.Length >= MinPlausibleHashLength && Base64Like.IsMatch(value));

    // Every single-quoted SQL string literal in a raw INSERT/UPDATE — the
    // shape a migrationBuilder.Sql(...) credential would actually take.
    private static readonly Regex SqlStringLiteral = new(@"'([^']*)'", RegexOptions.Compiled);

    // #245's whole contract is "exactly one migration, InitialCreate" — the
    // application has never been deployed, so a virgin database is the only
    // starting state that exists anywhere, and there is therefore never a
    // reason to add a second migration file instead of hand-folding a change
    // into InitialCreate (see that migration's own header comment, most
    // recently its #364 addendum). This is the automated fence: a PR that
    // adds e.g. "AddFoo.cs" beside InitialCreate.cs now fails here instead of
    // only getting flagged in review — which is exactly what happened to the
    // migration this test itself replaces (PR #399 shipped a second
    // migration; this guard did not yet exist to catch it).
    [Fact]
    public void ExactlyOneMigrationExists_AndItIsInitialCreate()
    {
        var migration = Assert.Single(AllMigrations());
        Assert.Equal("InitialCreate", migration.GetType().Name);
    }

    [Fact]
    public void NoMigration_EverInsertsIntoTheUsersTable()
    {
        var viaInsertData = AllInsertDataOperations()
            .Where(op => string.Equals(op.Table, "AspNetUsers", StringComparison.OrdinalIgnoreCase))
            .Select(op => $"InsertData -> {op.Table}");

        // Case-insensitive, optional quoting, tolerant of whitespace — matches
        // `INSERT INTO "AspNetUsers"`, `insert into AspNetUsers`, etc.
        var aspNetUsersInsert = new Regex(
            """insert\s+into\s+"?AspNetUsers"?""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var viaRawSql = AllSqlOperations()
            .Where(op => aspNetUsersInsert.IsMatch(op.Sql))
            .Select(op => $"Sql -> {Truncate(op.Sql)}");

        var offenders = viaInsertData.Concat(viaRawSql).ToList();

        Assert.True(offenders.Count == 0,
            "A migration inserts directly into AspNetUsers — that bakes a user (and its password hash) " +
            "into the repo, shipping every deployment the same publicly-known credential. The first admin " +
            "must only ever be created at runtime by the `bootstrap-admin` command. Offenders: " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void NoMigration_EverInsertsACredentialShapedColumn_IntoAnyTable()
    {
        var viaColumnName = AllInsertDataOperations()
            .Where(op => op.Columns.Any(c => CredentialShapedColumns.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .Select(op => $"InsertData -> {op.Table} (credential-shaped column name)");

        var viaSqlColumnName = AllSqlOperations()
            .Where(op => CredentialShapedColumns.Any(c => op.Sql.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .Select(op => $"Sql -> {Truncate(op.Sql)} (credential-shaped column name)");

        var offenders = viaColumnName.Concat(viaSqlColumnName).ToList();

        Assert.True(offenders.Count == 0,
            "A migration references a credential-shaped column " +
            $"({string.Join(", ", CredentialShapedColumns)}): {string.Join("; ", offenders)}.");
    }

    // Column-NAME-agnostic half of the guard (PR #339 review): a credential
    // could be smuggled in under any column, or as a bare literal in raw SQL
    // with no column name attached at all. This inspects the actual VALUE
    // shape instead.
    [Fact]
    public void NoMigration_EverInsertsAHashShapedValue_RegardlessOfColumnName()
    {
        var viaInsertDataValues = AllInsertDataOperations()
            .SelectMany(op => Enumerable.Range(0, op.Values.GetLength(0))
                .SelectMany(row => Enumerable.Range(0, op.Values.GetLength(1))
                    .Where(col => op.Values[row, col] is string s && LooksHashShaped(s))
                    .Select(col => $"InsertData -> {op.Table}.{op.Columns[col]} = {Truncate((string)op.Values[row, col]!)}")));

        var viaSqlLiterals = AllSqlOperations()
            .SelectMany(op => SqlStringLiteral.Matches(op.Sql)
                .Select(m => m.Groups[1].Value)
                .Where(LooksHashShaped)
                .Select(literal => $"Sql -> {Truncate(literal)}"));

        var offenders = viaInsertDataValues.Concat(viaSqlLiterals).ToList();

        Assert.True(offenders.Count == 0,
            "A migration embeds a value shaped like an Identity password hash (starts with " +
            $"'{IdentityHashV3Marker}', or is a long base64-shaped blob), independent of what column or " +
            $"context it's in: {string.Join("; ", offenders)}.");
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "…";

    // The positive assertion mirroring the negative ones above: the schema
    // DOES seed the static reference data #283 Part 1 promises — as
    // idempotent raw SQL, one INSERT per row, each column-value list free of
    // anything credential-shaped — and nothing more than that data.
    //
    // #245 — this used to target AddBaseReferenceDataAndMustChangePassword by
    // name. That migration is gone (squashed); its raw SQL was carried by
    // hand into InitialCreate, which is precisely the step where a statement
    // could have been dropped on the floor, so the assertions matter MORE
    // after the squash, not less. Resolved by SqlOperation content rather
    // than by migration name so a future migration can't quietly move them.
    [Fact]
    public void InitialCreateMigration_SeedsExactlyTheStaticReferenceRows()
    {
        // Every INSERT the migration history performs, wherever it lives.
        var sqlOps = AllSqlOperations()
            .Where(op => op.Sql.Contains("INSERT INTO", StringComparison.Ordinal))
            .ToList();

        int CountInserts(string table) =>
            sqlOps.Count(op => op.Sql.Contains($"INSERT INTO \"{table}\"", StringComparison.Ordinal));

        // Statement counts. The PER-KEY batches are one statement per row.
        // EggGrades is deliberately ONE statement covering all ten rows,
        // because its guard is WHOLE-SET ("does this account have any grade at
        // all") rather than per-name (PR #339 review round 2): a farm that
        // renamed the seeded "Small" to "Pullet" is invisible to a per-name
        // guard, which would then resurrect an active, saleable "Small"
        // beside it. A whole-set guard is only expressible as a single
        // INSERT ... SELECT over a VALUES list with one constant predicate.
        Assert.Equal(1, CountInserts("Accounts"));
        Assert.Equal(4, CountInserts("AspNetRoles"));
        Assert.Equal(1, CountInserts("EggGrades"));
        Assert.Equal(6, CountInserts("EggUnitConversions"));

        // That single grades statement still carries all ten rows — counted
        // by their fixed id literals, so a dropped VALUES tuple is caught
        // here rather than silently shipping a nine-grade catalog.
        var gradeSql = sqlOps.Single(
            op => op.Sql.Contains("INSERT INTO \"EggGrades\"", StringComparison.Ordinal)).Sql;
        Assert.Equal(10, Regex.Matches(gradeSql, "'0000000e-0000-0000-0000-0000000000[0-9]{2}'").Count);

        // And its guard is genuinely whole-set: the subquery must not look at
        // "Name" at all. A per-name guard necessarily compares it — this is
        // the cheap, Docker-free sentinel for the resurrection regression
        // (the behavioural fixtures that proved it lived in
        // MigrationUpgradePathTests's upgrade-from-an-old-seeder cases, which
        // the #245 squash retired along with the history they upgraded from).
        var gradeGuard = gradeSql[gradeSql.IndexOf("WHERE NOT EXISTS", StringComparison.Ordinal)..];
        Assert.DoesNotContain("\"Name\"", gradeGuard, StringComparison.Ordinal);

        // Every one of those inserts is a WHERE NOT EXISTS guard — kept
        // through the squash (PR #339): a bare unconditional INSERT would
        // re-introduce the "cannot re-run against a populated database"
        // regression that review caught.
        Assert.All(sqlOps, op => Assert.Contains("WHERE NOT EXISTS", op.Sql, StringComparison.Ordinal));

        // Nothing else — the whole point of "static reference data, no
        // runtime seeder" is that the migrations' raw SQL inserts touch
        // exactly these four tables: 12 statements carrying 21 rows
        // (1 + 4 + 10 + 6).
        Assert.Equal(12, sqlOps.Count);
    }

    // #245 — the four case-insensitive-uniqueness indexes are expression
    // (functional) indexes, which the EF model CANNOT express: they only
    // exist as raw SQL. Regenerating InitialCreate from the model therefore
    // drops them silently — losing four unique constraints and letting a farm
    // create "Layer Mash" beside "layer mash". They were caught here by
    // diffing pg_dump before/after the squash; this is the cheap, Docker-free
    // sentinel that keeps them from being lost the next time the migration
    // set is regenerated.
    [Theory]
    [InlineData("IX_EggGrades_AccountId_FarmId_LowerName", "\"EggGrades\" (\"AccountId\", \"FarmId\", lower(\"Name\"))")]
    [InlineData("IX_ExpenseCategories_NameCi", "\"ExpenseCategories\" (\"AccountId\", \"FarmId\", lower(\"Name\"))")]
    [InlineData("UX_InventoryItems_Account_Farm_LowerName", "\"InventoryItems\" (\"AccountId\", \"FarmId\", lower(\"Name\"))")]
    [InlineData("IX_Products_AccountId_LowerName", "\"Products\" (\"AccountId\", lower(\"Name\"))")]
    public void Migrations_StillCreateTheExpressionUniqueIndexes(string indexName, string targetClause)
    {
        var create = AllSqlOperations()
            .Select(op => op.Sql)
            .Where(sql => sql.Contains($"CREATE UNIQUE INDEX \"{indexName}\"", StringComparison.Ordinal))
            .ToList();

        var sql = Assert.Single(create);
        Assert.Contains(targetClause, sql, StringComparison.Ordinal);
    }
}
