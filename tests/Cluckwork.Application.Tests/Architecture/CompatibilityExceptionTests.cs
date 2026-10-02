namespace Cluckwork.Application.Tests.Architecture;

public sealed class CompatibilityExceptionTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("compatibility-exception-").FullName;
    private const string Symbol = "Cluckwork.Temp.Probe.Run";

    private const string FixtureDb = """
        using Cluckwork.Domain.Expenses;
        using Microsoft.EntityFrameworkCore;
        namespace Cluckwork.Temp;
        public class FixtureDb(DbContextOptions options) : DbContext(options)
        {
            public DbSet<Expense> Expenses => Set<Expense>();
            public DbSet<Cluckwork.Domain.Flocks.Flock> Flocks => Set<Cluckwork.Domain.Flocks.Flock>();
        }
        """;

    public CompatibilityExceptionTests() => WriteSource("Cluckwork.Infrastructure/FixtureDb.cs", FixtureDb);

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

    private IReadOnlyList<string> Evaluate(string rows = "", string financeContract = "\"Cluckwork.Temp.Finance.IFinanceModule\"")
    {
        var path = Path.Combine(_tempRoot, "module-ledger.json");
        File.WriteAllText(path, $$"""
            {
              "owners": {
                "Hub": { "kind": "platform", "namespaces": ["Cluckwork.Temp"] },
                "Finance": { "kind": "module", "namespaces": ["Cluckwork.Domain.Expenses", "Cluckwork.Temp.Finance"], "contract": [{{financeContract}}] },
                "Insights": { "kind": "module", "namespaces": ["Cluckwork.Temp.Insights"] }
              },
              "edges": [
                { "from": "Insights", "to": "Finance", "kind": "R", "reason": "test", "symbols": ["Cluckwork.Temp.Insights.Declared"] }
              ],
              "compatibilityExceptions": [{{rows}}]
            }
            """);
        return CompatibilityExceptionScanner.Evaluate(CompatibilityExceptionScanner.Scan(Path.Combine(_tempRoot, "src"), path));
    }

    private static string Row(string symbol = Symbol, string owner = "Hub", string deleteWhen = "#858") =>
        $$"""{ "symbol": "{{symbol}}", "reaches": "Finance", "owner": "{{owner}}", "reason": "test", "deleteWhen": "{{deleteWhen}}" }""";

    [Fact]
    public void UndeclaredRead_NamesMemberModuleAndLocationAndRendersRow()
    {
        WriteProbe("return db.Expenses.Count();");

        var failure = Assert.Single(Evaluate());
        Assert.Contains($"undeclared compatibility exception {Symbol} -> Finance", failure);
        Assert.Contains("src/Cluckwork.Infrastructure/Probe.cs:6", failure);
        Assert.Contains($"\"symbol\": \"{Symbol}\", \"reaches\": \"Finance\"", failure);
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

    [Theory]
    [InlineData("Cluckwork.Temp.Finance", "")]
    [InlineData("Cluckwork.Temp", ": Cluckwork.Temp.Finance.IExpenseStore")]
    public void ModulesOwnCodeAndPortImplementations_AreNotExceptions(string ns, string bases)
    {
        WriteSource("Cluckwork.Infrastructure/Port.cs", "namespace Cluckwork.Temp.Finance; public interface IExpenseStore { }");
        WriteProbe("return db.Expenses.Count();", ns, bases);

        Assert.Empty(Evaluate());
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

        var failures = Evaluate();
        Assert.Equal(allowed ? 0 : 1, failures.Count);
    }

    [Fact]
    public void UncontractedModulesTables_AreNotGuarded()
    {
        WriteProbe("return db.Flocks.Count();");

        Assert.Empty(Evaluate());
    }

    [Theory]
    [InlineData("\"reaches\": \"Finance\", \"owner\": \"Hub\", \"reason\": \"test\"", "blank or non-string 'deleteWhen'")]
    [InlineData("\"reaches\": \"Finance\", \"reason\": \"test\", \"deleteWhen\": \"#858\"", "blank or non-string 'owner'")]
    [InlineData("\"reaches\": \"Finance\", \"owner\": \"Hub\", \"deleteWhen\": \"#858\"", "blank or non-string 'reason'")]
    [InlineData("\"reaches\": \"Finance\", \"owner\": \"Hub\", \"reason\": \"test\", \"deleteWhen\": \"2026-12-31\"", "never a date")]
    [InlineData("\"reaches\": \"Finance\", \"owner\": \"Nobody\", \"reason\": \"test\", \"deleteWhen\": \"#858\"", "unknown owner 'Nobody'")]
    [InlineData("\"reaches\": \"Insights\", \"owner\": \"Hub\", \"reason\": \"test\", \"deleteWhen\": \"#858\"", "declares no contract")]
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
    public void SourceThatDoesNotBind_FailsClosed()
    {
        WriteProbe("return db.NoSuchSet.Count();");

        Assert.Contains(Evaluate(), f => f.StartsWith("compile:", StringComparison.Ordinal) && f.Contains("CS1061", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("return db.Expenses.Count();")]
    [InlineData("return db.Set<Cluckwork.Domain.Expenses.Expense>().Count();")]
    public void ReadInAProjectReferencingTheSemanticProject_IsFoundByName(string body)
    {
        WriteSource("Cluckwork.Infrastructure/Cluckwork.Infrastructure.csproj", "<Project />");
        WriteSource("Cluckwork.Api/Cluckwork.Api.csproj",
            """<Project><ItemGroup><ProjectReference Include="..\Cluckwork.Infrastructure\Cluckwork.Infrastructure.csproj" /></ItemGroup></Project>""");
        WriteSource("Cluckwork.Api/Verb.cs", $$"""
            namespace Cluckwork.Temp.Cli;
            public class Verb { public object Run(dynamic db) { {{body}} } }
            """);
        WriteSource("Cluckwork.Unrelated/Cluckwork.Unrelated.csproj", "<Project />");
        WriteSource("Cluckwork.Unrelated/Report.cs", """
            namespace Cluckwork.Temp.Unrelated;
            public class Report { public object Run(dynamic totals) { return totals.Expenses; } }
            """);

        var failure = Assert.Single(Evaluate());
        Assert.Contains("Cluckwork.Temp.Cli.Verb.Run -> Finance at src/Cluckwork.Api/Verb.cs:2", failure);
        Assert.Empty(Evaluate(Row("Cluckwork.Temp.Cli.Verb.Run")));
    }
}
