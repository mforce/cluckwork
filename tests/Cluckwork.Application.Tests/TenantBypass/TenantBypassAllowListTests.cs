namespace Cluckwork.Application.Tests.TenantBypass;

using System.Text;
using System.Text.Json;

// #536 Part 1 — allow-list semantics. Each behaviour gets its OWN named
// assertion against a temp source tree (never the repo), so the mutation
// matrix (Task 5) can aim its mutants at exactly these tests, and a real-tree
// mutant reds the real-tree test instead — one assertion, one lever (design
// M8, review M5).
public sealed class TenantBypassAllowListTests : IDisposable
{
    private const string ScopedLineageCte = """
        WITH fenced_tip AS (
            UPDATE refresh_tokens
            SET "RevokedAt" = @revokedAt
            WHERE "TokenHash" = @currentHash
              AND "UserId" = @rootUserId
              AND "AccountId" = @rootAccountId
              AND "IssuedEpoch" = @rootIssuedEpoch
            RETURNING 1
        ),
        severed_ancestors AS (
            UPDATE refresh_tokens AS ancestor
            SET "ReplacedByTokenHash" = NULL
            WHERE ancestor."TokenHash" = ANY (@ancestorHashes)
              AND ancestor."UserId" = @rootUserId
              AND ancestor."AccountId" = @rootAccountId
              AND ancestor."IssuedEpoch" = @rootIssuedEpoch
            RETURNING 1
        )
        SELECT TRUE
        """;

    private readonly string _tempRoot = Directory.CreateTempSubdirectory("t8-guard-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    private string WriteSource(string relativePath, string content)
    {
        var full = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private string WriteAllowList(params (string Symbol, string File, string Justification)[] entries)
    {
        var path = Path.Combine(_tempRoot, "allowlist.json");
        var json = JsonSerializer.Serialize(entries
            .Select(e => new { symbol = e.Symbol, file = e.File, justification = e.Justification }));
        File.WriteAllText(path, json);
        return path;
    }

    private static GuardReport Scan(string tempRoot, string allowListPath) =>
        GuardScanner.Scan(Path.Combine(tempRoot, "src"), allowListPath);

    // M8-M1: a bypass in a non-allow-listed method is UNEXCUSED.
    [Fact]
    public void UnlistedBypass_Fails()
    {
        WriteSource("src/A.cs", """
            namespace A;
            public class R
            {
                public int Bad() => Query().IgnoreQueryFilters().Count();
                private static System.Linq.IQueryable<int> Query() => System.Linq.Enumerable.Range(0, 1);
            }
            """);
        var report = Scan(_tempRoot, WriteAllowList());

        var failure = GuardScanner.Evaluate(report).FirstOrDefault(f => f.Contains("unexcused bypass"));
        Assert.False(string.IsNullOrEmpty(failure), "expected an unexcused-bypass failure");
        Assert.True(failure!.Contains("A.R.Bad"),
            "symbol should name the enclosing method A.R.Bad; full failure: " + failure);
        Assert.Contains("IgnoreQueryFilters", failure!);
    }

    // M8-M2: an allow-listed bypass is excused; deleting the entry makes it
    // unexcused again — same assertion, same lever.
    [Fact]
    public void AllowListedBypass_IsExcused()
    {
        WriteSource("src/A.cs", """
            namespace A;
            public class R
            {
                public int Good() => Query().IgnoreQueryFilters().Count();
                private static System.Linq.IQueryable<int> Query() => System.Linq.Enumerable.Range(0, 1);
            }
            """);
        // The symbol is the scanner's reconstructed display form (Namespace.Type
        // .Method(paramType paramText)) — see GuardScanner.ParameterTypes.
        var withEntry = Scan(_tempRoot, WriteAllowList(
            ("A.R.Good()", "src/A.cs", "test fixture")));
        var withFailures = GuardScanner.Evaluate(withEntry);
        Assert.True(withFailures.Count == 0,
            "expected excused, got: " + string.Join(" | ", withFailures));

        var withoutEntry = Scan(_tempRoot, WriteAllowList());
        Assert.Contains(GuardScanner.Evaluate(withoutEntry), f => f.Contains("unexcused bypass"));
    }

    // M8-M3: an entry matching zero sites is STALE and fails — a deleted
    // bypass must not leave a live exemption.
    [Fact]
    public void StaleEntry_Fails()
    {
        WriteSource("src/A.cs", """
            namespace A;
            public class R { public int X() => 1; }
            """);
        var report = Scan(_tempRoot, WriteAllowList(
            ("A.R.Gone()", "src/A.cs", "site was deleted but this entry was not")));

        var staleFailure = GuardScanner.Evaluate(report).FirstOrDefault(f => f.Contains("stale allow-list entry"));
        Assert.False(string.IsNullOrEmpty(staleFailure), "expected a stale-entry failure");
        Assert.Contains("A.R.Gone()", staleFailure!);
    }

    // M8-M4: a filter-free-set query without an AccountId comparison is
    // flagged; with one, it is not. SHAPE, not provenance (review M4/F4):
    // this proves the predicate exists, not that the compared value is the
    // resolved tenant.
    [Fact]
    public void MissingAccountIdCompare_Fails()
    {
        // The leg matches on the DbSet PROPERTY name as the model reports it.
        // `db.Users` is the property access; `db`-shaped receivers only (a
        // domain property named Users elsewhere is out of scope — stated in
        // the scanner).
        WriteSource("src/B.cs", """
            namespace B;
            public class R
            {
                public async Task<int> Bad(Microsoft.EntityFrameworkCore.DbSet<U> db)
                    => await db.Users.Where(u => u.Email == "x").CountAsync();
                public async Task<int> Good(Microsoft.EntityFrameworkCore.DbSet<U> db)
                    => await db.Users.Where(u => u.AccountId == Tenant.Id).CountAsync();
            }
            public class U { public Guid AccountId { get; set; } public string Email { get; set; } = ""; }
            public static class Tenant { public static Guid Id => default; }
            """);

        var occurrences = GuardScanner.ScanFilterFreeSet(Path.Combine(_tempRoot, "src"), ["Users"]);
        Assert.Equal(2, occurrences.Count);

        var bad = occurrences.Single(o => o.EnclosingSymbol.Contains(".Bad("));
        var good = occurrences.Single(o => o.EnclosingSymbol.Contains(".Good("));

        Assert.False(bad.PredicateHasAccountId, "query without an AccountId comparison must be flagged");
        Assert.True(good.PredicateHasAccountId, "query WITH an AccountId comparison must not be flagged");
    }

    // M8-M7: one extension-method wrapper must not defeat the guard. A method
    // that forwards IgnoreQueryFilters() is itself an occurrence, and its
    // callers are occurrences too — the wrapper is not a laundering step.
    [Fact]
    public void WrapperForwarding_Fails()
    {
        WriteSource("src/C.cs", """
            namespace C;
            public static class Ext
            {
                public static System.Linq.IQueryable<T> Unfiltered<T>(this System.Linq.IQueryable<T> q)
                    => q.IgnoreQueryFilters();
            }
            public class Caller
            {
                public System.Linq.IQueryable<int> Use() => Ext.Unfiltered(System.Linq.Enumerable.Range(0, 1));
            }
            """);
        var report = Scan(_tempRoot, WriteAllowList());
        var failures = GuardScanner.Evaluate(report);

        // BOTH sites must be reported, not just the wrapper definition (review
        // P1-3: the old test asserted only Ext.Unfiltered was reported, but the
        // CALLER Use() was not — allow-listing the wrapper left the caller
        // green, the exact laundering the test claimed to prevent). The scanner
        // now flags the call site of a forwarding method (forwards-bypass), so
        // the caller Caller.Use is an unexcused occurrence too.
        Assert.Contains(failures, f => f.Contains("Ext.Unfiltered"));
        Assert.Contains(failures, f => f.Contains("Caller.Use") || f.Contains("Use()"));
    }

    [Fact]
    public void LowLevelRawSqlBuild_IsDiscoveredAndClassified()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT TRUE";
                    var rawCommand = db.GetService<IRawSqlCommandBuilder>().Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        var occurrence = Assert.Single(report.Occurrences,
            o => o.Kind == BypassKind.RawSql
                && o.EnclosingSymbol == "LowLevel.Runner.ExecuteAsync()");
        Assert.Contains("IRawSqlCommandBuilder.Build", occurrence.Detail);
        Assert.Contains("SELECT TRUE", occurrence.RawSqlText);
        Assert.Empty(GuardScanner.Evaluate(report));
    }

    [Fact]
    public void LowLevelRawSqlBuild_IsDiscoveredFromGenericGetServiceLocal()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT TRUE";
                    var builder = db.GetService<IRawSqlCommandBuilder>();
                    var rawCommand = builder.Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        var occurrence = Assert.Single(report.Occurrences,
            o => o.Kind == BypassKind.RawSql
                && o.EnclosingSymbol == "LowLevel.Runner.ExecuteAsync()");
        Assert.Contains("IRawSqlCommandBuilder.Build", occurrence.Detail);
        Assert.Contains("SELECT TRUE", occurrence.RawSqlText);
        Assert.Empty(GuardScanner.Evaluate(report));
    }

    [Fact]
    public void LowLevelRawSqlBuild_IsDiscoveredFromNonGenericGetServiceLocal()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT TRUE";
                    var builder = (IRawSqlCommandBuilder)db.GetService(typeof(IRawSqlCommandBuilder));
                    var rawCommand = builder.Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        var occurrence = Assert.Single(report.Occurrences,
            o => o.Kind == BypassKind.RawSql
                && o.EnclosingSymbol == "LowLevel.Runner.ExecuteAsync()");
        Assert.Contains("IRawSqlCommandBuilder.Build", occurrence.Detail);
        Assert.Contains("SELECT TRUE", occurrence.RawSqlText);
        Assert.Empty(GuardScanner.Evaluate(report));
    }

    [Fact]
    public void LowLevelRawSqlBuild_IsDiscoveredFromAssignedGenericGetServiceLocal()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT TRUE";
                    IRawSqlCommandBuilder builder;
                    builder = db.GetService<IRawSqlCommandBuilder>();
                    var rawCommand = builder.Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        var occurrence = Assert.Single(report.Occurrences,
            o => o.Kind == BypassKind.RawSql
                && o.EnclosingSymbol == "LowLevel.Runner.ExecuteAsync()");
        Assert.Contains("IRawSqlCommandBuilder.Build", occurrence.Detail);
        Assert.Contains("SELECT TRUE", occurrence.RawSqlText);
        Assert.Empty(GuardScanner.Evaluate(report));
    }

    [Fact]
    public void LowLevelRawSqlBuild_IsDiscoveredFromAssignedNonGenericGetServiceLocal()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT TRUE";
                    IRawSqlCommandBuilder builder;
                    builder = (IRawSqlCommandBuilder)db.GetService(typeof(IRawSqlCommandBuilder));
                    var rawCommand = builder.Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        var occurrence = Assert.Single(report.Occurrences,
            o => o.Kind == BypassKind.RawSql
                && o.EnclosingSymbol == "LowLevel.Runner.ExecuteAsync()");
        Assert.Contains("IRawSqlCommandBuilder.Build", occurrence.Detail);
        Assert.Contains("SELECT TRUE", occurrence.RawSqlText);
        Assert.Empty(GuardScanner.Evaluate(report));
    }

    [Fact]
    public void LowLevelRawSqlBuild_RowLockWithoutAccountIdPredicateFails()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT * FROM refresh_tokens WHERE \"TokenHash\" = @hash FOR UPDATE";
                    var rawCommand = db.GetService<IRawSqlCommandBuilder>().Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        Assert.Contains(GuardScanner.Evaluate(report), failure =>
            failure.Contains("raw-SQL row lock missing an AccountId predicate (M4)", StringComparison.Ordinal)
            && failure.Contains("IRawSqlCommandBuilder.Build", StringComparison.Ordinal));
    }

    [Fact]
    public void LowLevelRawSqlBuild_RowLockIgnoresAccountIdInsideSingleQuotedLiteral()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT * FROM refresh_tokens WHERE \"TokenHash\" = 'tenant''s AccountId' FOR UPDATE";
                    var rawCommand = db.GetService<IRawSqlCommandBuilder>().Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        Assert.Contains(GuardScanner.Evaluate(report), failure =>
            failure.Contains("raw-SQL row lock missing an AccountId predicate (M4)", StringComparison.Ordinal)
            && failure.Contains("IRawSqlCommandBuilder.Build", StringComparison.Ordinal));
    }

    [Fact]
    public void LowLevelRawSqlBuild_QuotedAccountIdIdentifierScopesRowLock()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> ExecuteAsync()
                {
                    const string sql = "SELECT * FROM refresh_tokens WHERE \"AccountId\" = @accountId FOR UPDATE";
                    var rawCommand = db.GetService<IRawSqlCommandBuilder>().Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.ExecuteAsync()", "src/LowLevel.cs", "test fixture")));

        var occurrence = Assert.Single(report.Occurrences,
            o => o.Kind == BypassKind.RawSql
                && o.EnclosingSymbol == "LowLevel.Runner.ExecuteAsync()");
        Assert.Empty(GuardScanner.Evaluate(report));
    }

    [Fact]
    public void LowLevelRawSqlBuild_RequiresRelationalCommandExecution()
    {
        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public void Execute()
                {
                    const string sql = "SELECT TRUE";
                    _ = db.GetService<IRawSqlCommandBuilder>().Build(sql, [], db.Model);
                }
            }
            """);

        var report = Scan(_tempRoot, WriteAllowList(
            ("LowLevel.Runner.Execute()", "src/LowLevel.cs", "test fixture")));

        Assert.Contains(GuardScanner.Evaluate(report), failure =>
            failure.Contains("IRawSqlCommandBuilder.Build", StringComparison.Ordinal)
            && failure.Contains("RelationalCommand execution", StringComparison.Ordinal));
    }

    [Fact]
    public void LowLevelRawSqlBuild_RemovalOrMethodMoveFailsClassification()
    {
        const string classifiedSymbol = "LowLevel.Runner.ExecuteAsync()";
        var allowList = WriteAllowList(
            (classifiedSymbol, "src/LowLevel.cs", "test fixture"));

        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public Task<bool> ExecuteAsync() => Task.FromResult(true);
            }
            """);
        var removed = Scan(_tempRoot, allowList);
        Assert.Contains(GuardScanner.Evaluate(removed), failure =>
            failure.Contains("stale allow-list entry", StringComparison.Ordinal)
            && failure.Contains(classifiedSymbol, StringComparison.Ordinal));

        WriteSource("src/LowLevel.cs", """
            namespace LowLevel;
            public class Runner
            {
                public async Task<bool> MovedAsync()
                {
                    const string sql = "SELECT TRUE";
                    var rawCommand = db.GetService<IRawSqlCommandBuilder>().Build(sql, [], db.Model);
                    return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameters, ct) is true;
                }
            }
            """);
        var moved = Scan(_tempRoot, allowList);
        var movedFailures = GuardScanner.Evaluate(moved);
        Assert.Contains(movedFailures, failure =>
            failure.Contains("stale allow-list entry", StringComparison.Ordinal)
            && failure.Contains(classifiedSymbol, StringComparison.Ordinal));
        Assert.Contains(movedFailures, failure =>
            failure.Contains("unexcused bypass", StringComparison.Ordinal)
            && failure.Contains("LowLevel.Runner.MovedAsync()", StringComparison.Ordinal));
    }

    [Fact]
    public void LowLevelRawSqlScope_AcceptsBothScopedCteUpdateArms()
    {
        var violations = GuardScanner.FindScopedUpdateArmViolations(
            ScopedLineageCte,
            expectedUpdateArmCount: 2,
            ("UserId", "rootUserId"),
            ("AccountId", "rootAccountId"),
            ("IssuedEpoch", "rootIssuedEpoch"));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("fenced_tip", "AND \"UserId\" = @rootUserId", "UserId")]
    [InlineData("fenced_tip", "AND \"AccountId\" = @rootAccountId", "AccountId")]
    [InlineData("fenced_tip", "AND \"IssuedEpoch\" = @rootIssuedEpoch", "IssuedEpoch")]
    [InlineData("severed_ancestors", "AND ancestor.\"UserId\" = @rootUserId", "UserId")]
    [InlineData("severed_ancestors", "AND ancestor.\"AccountId\" = @rootAccountId", "AccountId")]
    [InlineData("severed_ancestors", "AND ancestor.\"IssuedEpoch\" = @rootIssuedEpoch", "IssuedEpoch")]
    public void LowLevelRawSqlScope_RemovingOwnerPredicateFromEitherArmFails(
        string arm, string predicate, string column)
    {
        var mutant = ScopedLineageCte.Replace(predicate, string.Empty, StringComparison.Ordinal);

        var violations = GuardScanner.FindScopedUpdateArmViolations(
            mutant,
            expectedUpdateArmCount: 2,
            ("UserId", "rootUserId"),
            ("AccountId", "rootAccountId"),
            ("IssuedEpoch", "rootIssuedEpoch"));

        Assert.Contains(violations, violation =>
            violation.Contains(arm, StringComparison.Ordinal)
            && violation.Contains(column, StringComparison.Ordinal));
    }
}
