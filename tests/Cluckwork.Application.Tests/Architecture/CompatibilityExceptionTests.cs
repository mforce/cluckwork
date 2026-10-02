namespace Cluckwork.Application.Tests.Architecture;

public sealed class CompatibilityExceptionTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("compatibility-exception-").FullName;
    private const string Symbol = "Cluckwork.Temp.Probe.Run";
    private const string FinanceTables = "\"Expenses\", \"ExpenseCategories\"";
    private const string Tables = $"\"Finance\": [{FinanceTables}], \"Insights\": [\"Flocks\"]";

    private const string FixtureDb = """
        using Cluckwork.Domain.Expenses;
        using Microsoft.EntityFrameworkCore;
        namespace Cluckwork.Temp;
        public class FixtureDb(DbContextOptions options) : DbContext(options)
        {
            public DbSet<Expense> Expenses => Set<Expense>();
            public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
            public DbSet<Cluckwork.Domain.Flocks.Flock> Flocks => Set<Cluckwork.Domain.Flocks.Flock>();
        }
        """;

    public CompatibilityExceptionTests()
    {
        WriteSource("Cluckwork.Infrastructure/FixtureDb.cs", FixtureDb);
        WriteSource("Cluckwork.Infrastructure/Port.cs", "namespace Cluckwork.Temp.Finance; public interface IExpenseStore { }");
    }

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private void WriteSource(string relativePath, string content)
    {
        var full = Path.Combine(_tempRoot, "src", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteProbe(string body, string ns = "Cluckwork.Temp", string bases = "") => WriteSource(
        "Cluckwork.Infrastructure/Probe.cs", $$"""
            using Cluckwork.Domain.Expenses;
            using Microsoft.EntityFrameworkCore;
            namespace {{ns}};
            public class Probe(Cluckwork.Temp.FixtureDb db) {{bases}}
            {
                public object Run() { {{body}} }
            }
            """);

    private void WriteApi(string source)
    {
        WriteSource("Cluckwork.Api/Cluckwork.Api.csproj",
            """<Project><ItemGroup><ProjectReference Include="..\Cluckwork.Infrastructure\Cluckwork.Infrastructure.csproj" /></ItemGroup></Project>""");
        WriteSource("Cluckwork.Api/Verb.cs", source);
    }

    private IReadOnlyList<string> Evaluate(string rows = "", string implementations = "", string tables = Tables)
    {
        var path = Path.Combine(_tempRoot, "module-ledger.json");
        File.WriteAllText(path, $$"""
            {
              "owners": {
                "Hub": { "kind": "platform", "namespaces": ["Cluckwork.Temp"] },
                "Finance": { "kind": "module", "namespaces": ["Cluckwork.Domain.Expenses", "Cluckwork.Temp.Finance"],
                  "contract": ["Cluckwork.Temp.Finance.IFinanceModule"], "implementations": [{{implementations}}] },
                "Insights": { "kind": "module", "namespaces": ["Cluckwork.Temp.Insights"] }
              },
              "edges": [
                { "from": "Insights", "to": "Finance", "kind": "R", "reason": "test", "symbols": ["Cluckwork.Temp.Insights.Declared"] }
              ],
              "tables": { {{tables}} },
              "compatibilityExceptions": [{{rows}}]
            }
            """);
        return CompatibilityExceptionScanner.Evaluate(CompatibilityExceptionScanner.Scan(Path.Combine(_tempRoot, "src"), path));
    }

    private static string Row(string symbol = Symbol, string tables = "\"Expenses\"", string owner = "Hub", string deleteWhen = "#858") =>
        $$"""{ "symbol": "{{symbol}}", "reaches": "Finance", "tables": [{{tables}}], "owner": "{{owner}}", "reason": "test", "deleteWhen": "{{deleteWhen}}" }""";

    [Fact]
    public void UndeclaredRead_NamesMemberModuleTableAndLocationAndRendersRow()
    {
        WriteProbe("return db.Expenses.Count();");

        var failure = Assert.Single(Evaluate());
        Assert.Contains($"undeclared compatibility exception {Symbol} -> Finance", failure);
        Assert.Contains("src/Cluckwork.Infrastructure/Probe.cs:6", failure);
        Assert.Contains($"\"symbol\": \"{Symbol}\", \"reaches\": \"Finance\", \"tables\": [\"Expenses\"]", failure);
        Assert.Empty(Evaluate(Row()));
    }

    [Theory]
    [InlineData("return db.Set<Expense>().Count();")]
    [InlineData("var alias = db; return alias.Expenses.Count();")]
    [InlineData("Func<int> count = () => db.Expenses.Count(); return count();")]
    [InlineData("int Count() => db.Expenses.Count(); return Count();")]
    [InlineData("return db.Expenses.Where(e => e.Description == \"x\").ToList();")]
    public void EveryShapeOfDbSetRead_IsAttributedToTheEnclosingMember(string body)
    {
        WriteProbe(body);

        Assert.Contains($"undeclared compatibility exception {Symbol} -> Finance", Assert.Single(Evaluate()));
    }

    [Fact]
    public void ReadInAPropertyOrNestedType_IsKeyedByThatMember()
    {
        WriteSource("Cluckwork.Infrastructure/Probe.cs", """
            namespace Cluckwork.Temp;
            public class Outer(FixtureDb db)
            {
                public int Total => db.Expenses.Count();
                public class Inner(FixtureDb db) { public int Run() => db.Expenses.Count(); }
            }
            """);

        var failures = Evaluate();
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("Cluckwork.Temp.Outer.Total -> Finance", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Cluckwork.Temp.Outer.Inner.Run -> Finance", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLedgersTableOwner_DecidesTheModuleReached()
    {
        WriteProbe("return db.ExpenseCategories.Count();");

        Assert.Single(Evaluate());
        Assert.Empty(Evaluate(tables: "\"Finance\": [\"Expenses\"], \"Insights\": [\"Flocks\", \"ExpenseCategories\"]"));
    }

    [Fact]
    public void ModulesOwnCode_IsNotAnException()
    {
        WriteProbe("return db.Expenses.Count();", "Cluckwork.Temp.Finance");

        Assert.Empty(Evaluate());
    }

    [Fact]
    public void ImplementingAModuleInterface_IsTrustedOnlyWhenTheLedgerListsTheType()
    {
        WriteProbe("return db.Expenses.Count();", bases: ": Cluckwork.Temp.Finance.IExpenseStore");

        Assert.Contains($"undeclared compatibility exception {Symbol} -> Finance", Assert.Single(Evaluate()));
        Assert.Empty(Evaluate(implementations: "\"Cluckwork.Temp.Probe\""));
    }

    [Fact]
    public void ListedImplementation_DoesNotCoverItsNestedTypes()
    {
        WriteSource("Cluckwork.Infrastructure/Probe.cs", """
            namespace Cluckwork.Temp;
            public class Probe(FixtureDb db) : Finance.IExpenseStore
            {
                public int Run() => db.Expenses.Count();
                public class Inner(FixtureDb db) { public int Run() => db.Expenses.Count(); }
            }
            """);

        Assert.Contains("Cluckwork.Temp.Probe.Inner.Run -> Finance",
            Assert.Single(Evaluate(implementations: "\"Cluckwork.Temp.Probe\"")));
    }

    [Theory]
    [InlineData("\"Cluckwork.Temp.Missing\"", "is not declared")]
    [InlineData("\"Cluckwork.Temp.FixtureDb\"", "implements none of Finance's interfaces")]
    public void ImplementationThatIsNotAPort_FailsTheRegistry(string implementation, string expected)
    {
        Assert.Contains(Evaluate(implementations: implementation),
            f => f.StartsWith("registry:", StringComparison.Ordinal) && f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ImplementationThatReadsNothing_FailsTheRegistry()
    {
        WriteProbe("return 0;", bases: ": Cluckwork.Temp.Finance.IExpenseStore");

        Assert.Contains(Evaluate(implementations: "\"Cluckwork.Temp.Probe\""),
            f => f.Contains("reads none of Finance's tables", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Declared", true)]
    [InlineData("Undeclared", false)]
    public void PeerModuleRead_IsAllowedOnlyForATypeItsEdgeNames(string type, bool allowed)
    {
        WriteSource("Cluckwork.Infrastructure/Queries.cs", $$"""
            namespace Cluckwork.Temp.Insights;
            public class {{type}}(Cluckwork.Temp.FixtureDb db) { public int Run() => db.Expenses.Count(); }
            """);

        Assert.Equal(allowed ? 0 : 1, Evaluate().Count);
    }

    [Fact]
    public void DbSetGetterThatQueries_IsNotADeclaration()
    {
        WriteSource("Cluckwork.Infrastructure/FixtureDb.cs", FixtureDb.Replace(
            "public DbSet<Expense> Expenses => Set<Expense>();",
            "public DbSet<Expense> Expenses { get { _ = Set<Expense>().Count(); return Set<Expense>(); } }",
            StringComparison.Ordinal));

        Assert.Contains("undeclared compatibility exception Cluckwork.Temp.FixtureDb.Expenses -> Finance", Assert.Single(Evaluate()));
    }

    [Fact]
    public void UncontractedModulesTables_AreNotGuarded()
    {
        WriteProbe("return db.Flocks.Count();");

        Assert.Empty(Evaluate());
    }

    [Fact]
    public void RegisteredMemberReadingATableItsRowDoesNotName_Fails()
    {
        WriteProbe("_ = db.ExpenseCategories.Count(); return db.Expenses.Count();");

        var failure = Assert.Single(Evaluate(Row()));
        Assert.Contains($"{Symbol} -> Finance reads table 'ExpenseCategories'", failure);
        Assert.Empty(Evaluate(Row(tables: FinanceTables)));
    }

    [Fact]
    public void RowNamingATableTheMemberNoLongerReads_IsStale()
    {
        WriteProbe("return db.Expenses.Count();");

        Assert.Contains("stale table 'ExpenseCategories'", Assert.Single(Evaluate(Row(tables: FinanceTables))));
    }

    [Theory]
    [InlineData("\"reaches\": \"Finance\", \"tables\": [\"Expenses\"], \"owner\": \"Hub\", \"reason\": \"test\"", "blank or non-string 'deleteWhen'")]
    [InlineData("\"reaches\": \"Finance\", \"tables\": [\"Expenses\"], \"reason\": \"test\", \"deleteWhen\": \"#858\"", "blank or non-string 'owner'")]
    [InlineData("\"reaches\": \"Finance\", \"tables\": [\"Expenses\"], \"owner\": \"Hub\", \"deleteWhen\": \"#858\"", "blank or non-string 'reason'")]
    [InlineData("\"reaches\": \"Finance\", \"tables\": [], \"owner\": \"Hub\", \"reason\": \"test\", \"deleteWhen\": \"#858\"", "names no tables")]
    [InlineData("\"reaches\": \"Finance\", \"tables\": [\"Flocks\"], \"owner\": \"Hub\", \"reason\": \"test\", \"deleteWhen\": \"#858\"", "do not give to Finance")]
    [InlineData("\"reaches\": \"Finance\", \"tables\": [\"Expenses\"], \"owner\": \"Hub\", \"reason\": \"test\", \"deleteWhen\": \"2026-12-31\"", "never a date")]
    [InlineData("\"reaches\": \"Finance\", \"tables\": [\"Expenses\"], \"owner\": \"Nobody\", \"reason\": \"test\", \"deleteWhen\": \"#858\"", "unknown owner 'Nobody'")]
    [InlineData("\"reaches\": \"Insights\", \"tables\": [\"Flocks\"], \"owner\": \"Hub\", \"reason\": \"test\", \"deleteWhen\": \"#858\"", "declares no contract")]
    public void IncompleteRow_FailsTheRegistry(string fields, string expected)
    {
        WriteProbe("return db.Expenses.Count();");

        Assert.Contains(Evaluate($$"""{ "symbol": "{{Symbol}}", {{fields}} }"""),
            f => f.StartsWith("registry:", StringComparison.Ordinal) && f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateRow_FailsTheRegistry()
    {
        WriteProbe("return db.Expenses.Count();");

        Assert.Contains(Evaluate(Row() + "," + Row()), f => f.Contains("duplicate compatibilityExceptions row", StringComparison.Ordinal));
    }

    [Fact]
    public void RowWhoseReadIsGone_IsStale()
    {
        WriteProbe("return 0;");

        var failure = Assert.Single(Evaluate(Row()));
        Assert.Contains($"stale compatibility exception {Symbol} -> Finance", failure);
        Assert.Contains("#858", failure);
    }

    [Fact]
    public void RowForAMemberTheModuleNowOwns_IsStale()
    {
        WriteProbe("return db.Expenses.Count();", "Cluckwork.Temp.Finance");

        Assert.Contains(Evaluate(Row("Cluckwork.Temp.Finance.Probe.Run")),
            f => f.StartsWith("stale compatibility exception", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericDbSetHelper_FailsClosed()
    {
        WriteSource("Cluckwork.Infrastructure/Probe.cs", """
            namespace Cluckwork.Temp;
            public static class Helper
            {
                public static int Count<T>(Microsoft.EntityFrameworkCore.DbContext db) where T : class => db.Set<T>().Count();
                public static int Run(FixtureDb db) => Count<Cluckwork.Domain.Expenses.Expense>(db);
            }
            """);

        Assert.Contains("of a type parameter", Assert.Single(Evaluate()));
    }

    [Fact]
    public void SourceThatDoesNotBind_FailsClosed()
    {
        WriteProbe("return db.NoSuchSet.Count();");

        Assert.Contains(Evaluate(), f => f.StartsWith("compile:", StringComparison.Ordinal) && f.Contains("CS1061", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("return db.Expenses.Count();")]
    [InlineData("return db.Set<E>().Count();")]
    [InlineData("return Rows.Count();")]
    public void ReadInAProjectReferencingTheSemanticProject_IsBoundSemantically(string body)
    {
        WriteApi($$"""
            using E = Cluckwork.Domain.Expenses.Expense;
            namespace Cluckwork.Temp.Cli;
            public class Verb(Cluckwork.Temp.FixtureDb db)
            {
                private Microsoft.EntityFrameworkCore.DbSet<E> Rows => db.Set<E>();
                public object Run() { {{body}} }
            }
            """);

        Assert.Contains("Cluckwork.Temp.Cli.Verb.Run -> Finance at src/Cluckwork.Api/Verb.cs:6",
            Assert.Single(Evaluate(Row("Cluckwork.Temp.Cli.Verb.Rows"))));
    }

    [Fact]
    public void NamesThatOnlyLookLikeAGuardedSet_AreNotReads()
    {
        WriteApi("""
            namespace Cluckwork.Temp.Cli;
            public record Totals(int Expenses);
            public class Verb
            {
                public object Run(Totals totals) => (totals.Expenses, nameof(Cluckwork.Domain.Expenses.Expense), new HashSet<int>());
            }
            """);

        Assert.Empty(Evaluate());
    }

    [Fact]
    public void UnboundCandidateInAReferencingProject_FailsClosed()
    {
        WriteApi("""
            namespace Cluckwork.Temp.Cli;
            public class Verb { public object Run(MissingContext db) => db.Expenses; }
            """);

        Assert.Contains("unresolved: Cluckwork.Temp.Cli.Verb.Run at src/Cluckwork.Api/Verb.cs:2", Assert.Single(Evaluate()));
    }

    [Fact]
    public void ProjectThatDoesNotReferenceTheSemanticProject_IsNotWalked()
    {
        WriteSource("Cluckwork.Unrelated/Cluckwork.Unrelated.csproj", "<Project />");
        WriteSource("Cluckwork.Unrelated/Report.cs", """
            namespace Cluckwork.Temp.Unrelated;
            public class Report { public object Run(MissingContext db) => db.Expenses; }
            """);

        Assert.Empty(Evaluate());
    }
}
