namespace Cluckwork.Api.IntegrationTests;

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    // #407 CUTOVER — this fence used to assert `Assert.Single(AllMigrations())`.
    //
    // #245's contract was "exactly one migration, InitialCreate", valid only
    // while the application had never been deployed: a virgin database was the
    // only starting state anywhere, so hand-folding a schema change into
    // InitialCreate rewrote a file no deployed database had applied yet. PR
    // #407 is the LAST change permitted to do that. From the commit that
    // merges it, a real database exists that has already applied
    // InitialCreate, and EF will never re-run it — so a hand-folded column is
    // a column that silently does not exist in production. (That failure mode
    // is not hypothetical: it is exactly what #399's amendment did to the dev
    // database, surfacing as a broken login rather than as a migration error.)
    //
    // What replaces it is NOT "no fence". Two things must still hold, and both
    // are cheap to break by accident:
    //
    //   1. InitialCreate stays FIRST and keeps its recorded id. Regenerating
    //      it (a fresh `dotnet ef migrations add InitialCreate`) mints a NEW
    //      timestamp, which reorders it behind migrations that depend on it
    //      and makes __EFMigrationsHistory disagree with the assembly on every
    //      deployed database. Pinning the id is what makes that a red test
    //      rather than a failed deploy.
    //   2. Ordering is asserted on the MIGRATION ID, never on list position.
    //      AllMigrations() is built from Assembly.GetTypes(), whose order is
    //      explicitly unspecified — a positional check would pass or fail on
    //      reflection ordering, which is the definition of a flaky guard.
    //
    // Everything else in this file deliberately scans EVERY migration
    // (AllInsertDataOperations/AllSqlOperations already fan out across the
    // whole history), so the credential and reference-data guarantees below
    // extend to post-cutover migrations for free — they were written that way
    // from the start, and that is what makes relaxing this one test safe.
    private const string InitialCreateMigrationId = "20260801190854_InitialCreate";

    [Fact]
    public void InitialCreate_IsStillTheFirstMigration_AndKeepsItsRecordedId()
    {
        var ordered = AllMigrations()
            .Select(m => (Migration: m, Id: MigrationIdOf(m)))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(ordered);
        Assert.Equal("InitialCreate", ordered[0].Migration.GetType().Name);
        Assert.Equal(InitialCreateMigrationId, ordered[0].Id);
    }

    private static string MigrationIdOf(Migration migration) =>
        migration.GetType()
            .GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
            .Cast<MigrationAttribute>()
            .Single()
            .Id;

    // #407 codex review (P1) — pinning InitialCreate's NAME and ID is not the
    // same as freezing it, and the gap is exactly the regression the cutover
    // exists to prevent. A later PR that edits InitialCreate.cs to add a column
    // (instead of adding a second migration) changes neither the class name nor
    // the MigrationAttribute, so the identity test above stays green; the other
    // tests in this file only look at credential-shaped inserts, the
    // reference-data SQL, and the four expression indexes. An ordinary
    // AddColumn/CreateTable edit was invisible to all of them. A database that
    // already recorded this ID skips the edit, and the app then runs against a
    // schema element that does not exist.
    //
    // So the operations themselves are fingerprinted. Any added, removed, or
    // retyped column/table/index/raw-SQL statement changes the digest below and
    // fails here, naming the fix.
    //
    // Deliberately a STRUCTURAL description, not a serialization of EF's
    // operation objects: the latter would break on an EF version bump that
    // touched an internal property nobody edited, training people to re-baseline
    // the constant on sight — which is how a fence like this dies. Describe()
    // reads only the properties that define what the migration DOES.
    //
    // Its `_` fallback collapses unusual operation types to their type name, so
    // two different DropColumns would describe identically. That is covered by
    // pinning the COUNT alongside the digest: an added or removed operation
    // moves the count even when its description is coarse. Stated plainly
    // because it is a real limit, not a claim of totality.
    //
    // Legitimately changing InitialCreate should now be impossible. If this
    // fails, the fix is almost always "add a migration instead" — re-baselining
    // the constant is the wrong move unless the change genuinely predates any
    // deployed database.
    private const int InitialCreateOperationCount = 114;
    private const string InitialCreateOperationDigest =
        "320493300753dc57278ec990ff678e8fd7fed1a82ac438ea8563acda65756efc";

    [Fact]
    public void InitialCreate_OperationsAreFrozen()
    {
        var initialCreate = AllMigrations()
            .Single(m => m.GetType().Name == "InitialCreate");

        var descriptions = initialCreate.UpOperations.Select(Describe).ToList();
        var digest = Sha256Hex(string.Join("\n", descriptions));

        Assert.True(
            InitialCreateOperationCount == descriptions.Count
                && string.Equals(InitialCreateOperationDigest, digest, StringComparison.Ordinal),
            $"""
             InitialCreate's operations changed (count {descriptions.Count}, digest {digest}).

             Since PR #407 this migration is FROZEN: a database that already applied
             it will never re-run it, so a column folded in here silently does not
             exist in production and surfaces as broken behaviour, not as an error.

             Add a new migration instead:
               dotnet ef migrations add <Name> -p src/Cluckwork.Infrastructure -s src/Cluckwork.Api

             Only re-baseline the two constants above if this change genuinely
             predates every database that has applied {InitialCreateMigrationId}.
             """);
    }

    // #407 codex review round 2 (P1) — the hand-written switch this replaces
    // listed properties per operation type, and that approach missed something
    // on every pass. It captured a column's name/type/nullability but NOT its
    // default, nor a CreateTable's nested primary key, foreign keys, unique
    // constraints, or a foreign key's OnDelete. So changing
    // `AspNetUsers.CredentialEpoch` from `defaultValue: 1` to `0` — a value
    // #364 says is PERMANENTLY RETIRED — left the count and every emitted
    // description identical, and the freeze test green.
    //
    // Enumerating harder is the same bet that already lost twice. This walks
    // every public readable property instead, recursing into nested operations,
    // column definitions, annotations and sequences, so a schema-defining
    // property is included because it EXISTS, not because someone remembered
    // it. Adding a property to an operation type cannot silently escape the
    // digest.
    //
    // Determinism matters as much as completeness: properties are ordered by
    // NAME, never by reflection order, for the same reason the ordering test
    // above sorts on the migration id — Type.GetProperties() is explicitly
    // unspecified, and a digest built on it would churn between runs.
    //
    // The EF-version-churn concern that motivated the original structural
    // choice still applies and is accepted deliberately: this reads EF's
    // PUBLIC operation model (the documented shape of a migration), not
    // internals, and a genuine change there is worth a human look. The failure
    // message says re-baselining is almost never the right response.
    private static string Describe(MigrationOperation operation) =>
        $"{operation.GetType().Name}({DescribeValue(operation, depth: 0)})";

    private const int MaxDescribeDepth = 6;

    private static string DescribeValue(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return $"'{s}'";
            case bool or char or decimal or double or float or int or long or short
                or uint or ulong or ushort or byte or sbyte or Guid or DateTime or DateTimeOffset:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?";
            case Enum e:
                return $"{e.GetType().Name}.{e}";
        }

        if (depth >= MaxDescribeDepth) return $"<depth:{value.GetType().Name}>";

        if (value is IAnnotation annotation)
            return $"@{annotation.Name}={DescribeValue(annotation.Value, depth + 1)}";

        if (value is IEnumerable enumerable)
        {
            var items = enumerable.Cast<object?>().Select(v => DescribeValue(v, depth + 1));
            // NOT sorted: for columns, key columns and operation lists the ORDER
            // is itself schema (a composite key's column order changes the
            // index). Sorting here would hide a reordering.
            return $"[{string.Join(',', items)}]";
        }

        // #407 codex review round 3 (P1) — annotations are reached through
        // GetAnnotations(), a METHOD, so a walk over public non-indexed
        // PROPERTIES never sees them. The previous version had an IAnnotation
        // branch and a comment claiming annotations were covered; the branch was
        // unreachable and the comment was simply false. Proven, not argued:
        // with that version in place, flipping InitialCreate's
        // Npgsql:ValueGenerationStrategy from IdentityByDefaultColumn to
        // IdentityAlwaysColumn — a material identity-generation change — left
        // the freeze test GREEN.
        //
        // Enumerated explicitly and FIRST, so every annotatable in the graph
        // (the operation itself, and each AddColumnOperation nested in a
        // CreateTable) contributes. Sorted by name: IReadOnlyAnnotatable
        // promises no order, and an unsorted digest would churn between runs.
        var annotations = value is IReadOnlyAnnotatable annotatable
            ? annotatable.GetAnnotations()
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => $"@{a.Name}={DescribeValue(a.Value, depth + 1)}")
                .ToList()
            : [];

        var members = value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={DescribeValue(SafeGet(p, value), depth + 1)}");

        return string.Join(';', annotations.Concat(members));
    }

    private static object? SafeGet(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
        catch (Exception ex)
        {
            // A property that throws must not be silently equivalent to null —
            // that would be a hole in the digest. Record the failure itself.
            return $"<threw:{ex.GetType().Name}>";
        }
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

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
