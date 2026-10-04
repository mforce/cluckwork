using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class CompatibilityExceptionTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("compatibility-exception-").FullName;
    private const string Symbol = "Cluckwork.Temp.Probe.Run";
    private static readonly string[] FinanceTables = ["Expenses", "ExpenseCategories"];
    private static readonly TableClaim[] Tables =
        [new("Finance", "Expenses"), new("Finance", "ExpenseCategories"), new("Insights", "Flocks")];

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

    private IReadOnlyList<string> Evaluate(IReadOnlyList<CompatibilityException>? rows = null,
        IReadOnlyList<string>? implementations = null, IReadOnlyList<TableClaim>? tables = null)
    {
        var ledger = ModuleLedger.Validate(new ModuleLedger(
            [
                new("Hub", "platform", ["Cluckwork.Temp"], []),
                new("Finance", "module", ["Cluckwork.Domain.Expenses", "Cluckwork.Temp.Finance"], [])
                {
                    Contract = ["Cluckwork.Temp.Finance.IFinanceModule"], Implementations = implementations ?? [],
                },
                new("Insights", "module", ["Cluckwork.Temp.Insights"], []),
            ],
            [new("Insights", "Finance", "R", "test", ["Cluckwork.Temp.Insights.Declared"])],
            [])
        {
            Tables = tables ?? Tables,
            CompatibilityExceptions = rows ?? [],
        });
        return CompatibilityExceptionScanner.Evaluate(CompatibilityExceptionScanner.Scan(Path.Combine(_tempRoot, "src"), ledger));
    }

    private static CompatibilityException Row(string symbol = Symbol, IReadOnlyList<string>? tables = null,
        string owner = "Hub", string deleteWhen = "#858") =>
        new(symbol, "Finance", tables ?? ["Expenses"], owner, "test", deleteWhen);

    [Fact]
    public void UndeclaredRead_NamesMemberModuleTableAndLocationAndRendersRow()
    {
        WriteProbe("return db.Expenses.Count();");

        var failure = Assert.Single(Evaluate());
        Assert.Contains($"undeclared compatibility exception {Symbol} -> Finance", failure);
        Assert.Contains("src/Cluckwork.Infrastructure/Probe.cs:6", failure);
        Assert.EndsWith($"add this row to RealModuleLedger.CompatibilityExceptions and fill in owner, reason and deleteWhen:\n" +
            $"new(\"{Symbol}\", \"Finance\", [\"Expenses\"], \"\", \"\", \"#\"),", failure);
        Assert.Empty(Evaluate([Row()]));
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
        Assert.Empty(Evaluate(tables: [new("Finance", "Expenses"), new("Insights", "Flocks"), new("Insights", "ExpenseCategories")]));
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
        Assert.Empty(Evaluate(implementations: ["Cluckwork.Temp.Probe"]));
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
            Assert.Single(Evaluate(implementations: ["Cluckwork.Temp.Probe"])));
    }

    [Fact]
    public void ListedImplementation_DoesNotCoverAGenericTypeOfTheSameName()
    {
        WriteSource("Cluckwork.Infrastructure/Probe.cs", """
            namespace Cluckwork.Temp;
            public class Probe(FixtureDb db) : Finance.IExpenseStore { public int Run() => db.Expenses.Count(); }
            public static class Probe<T> { public static int Run(FixtureDb db) => db.Expenses.Count(); }
            """);

        Assert.Contains("undeclared compatibility exception Cluckwork.Temp.Probe<T>.Run -> Finance",
            Assert.Single(Evaluate(implementations: ["Cluckwork.Temp.Probe"])));
    }

    [Theory]
    [InlineData("Cluckwork.Temp.Missing", "is not declared")]
    [InlineData("Cluckwork.Temp.FixtureDb", "implements none of Finance's interfaces")]
    public void ImplementationThatIsNotAPort_FailsTheRegistry(string implementation, string expected)
    {
        Assert.Contains(Evaluate(implementations: [implementation]),
            f => f.StartsWith("registry:", StringComparison.Ordinal) && f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ImplementationThatReadsNothing_FailsTheRegistry()
    {
        WriteProbe("return 0;", bases: ": Cluckwork.Temp.Finance.IExpenseStore");

        Assert.Contains(Evaluate(implementations: ["Cluckwork.Temp.Probe"]),
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

    [Theory]
    [InlineData("public DbSet<Expense> Expenses { get { _ = Set<Expense>().Count(); return Set<Expense>(); } }")]
    [InlineData("public DbSet<Expense> Expenses => (Set<Expense>().Count(), Set<Expense>()).Item2;")]
    public void DbSetGetterThatQueries_IsNotADeclaration(string getter)
    {
        WriteSource("Cluckwork.Infrastructure/FixtureDb.cs", FixtureDb.Replace(
            "public DbSet<Expense> Expenses => Set<Expense>();", getter, StringComparison.Ordinal));

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

        var failure = Assert.Single(Evaluate([Row()]));
        Assert.Contains($"{Symbol} -> Finance reads table 'ExpenseCategories'", failure);
        Assert.Empty(Evaluate([Row(tables: FinanceTables)]));
    }

    [Fact]
    public void RowNamingATableTheMemberNoLongerReads_IsStale()
    {
        WriteProbe("return db.Expenses.Count();");

        Assert.Contains("stale table 'ExpenseCategories'", Assert.Single(Evaluate([Row(tables: FinanceTables)])));
    }

    [Theory]
    [InlineData("Finance", "Expenses", "Hub", "test", "", "blank or non-string 'deleteWhen'")]
    [InlineData("Finance", "Expenses", "", "test", "#858", "blank or non-string 'owner'")]
    [InlineData("Finance", "Expenses", "Hub", "", "#858", "blank or non-string 'reason'")]
    [InlineData("Finance", "", "Hub", "test", "#858", "names no tables")]
    [InlineData("Finance", "Flocks", "Hub", "test", "#858", "do not give to Finance")]
    [InlineData("Finance", "Expenses", "Hub", "test", "2026-12-31", "never a date")]
    [InlineData("Finance", "Expenses", "Nobody", "test", "#858", "unknown owner 'Nobody'")]
    [InlineData("Insights", "Flocks", "Hub", "test", "#858", "declares no contract")]
    public void IncompleteRow_FailsTheRegistry(
        string reaches, string table, string owner, string reason, string deleteWhen, string expected)
    {
        WriteProbe("return db.Expenses.Count();");

        Assert.Contains(Evaluate([new(Symbol, reaches, table == "" ? [] : [table], owner, reason, deleteWhen)]),
            f => f.StartsWith("registry:", StringComparison.Ordinal) && f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateRow_FailsTheRegistry()
    {
        WriteProbe("return db.Expenses.Count();");

        Assert.Contains(Evaluate([Row(), Row()]), f => f.Contains("duplicate compatibilityExceptions row", StringComparison.Ordinal));
    }

    [Fact]
    public void RowWhoseReadIsGone_IsStale()
    {
        WriteProbe("return 0;");

        var failure = Assert.Single(Evaluate([Row()]));
        Assert.Contains($"stale compatibility exception {Symbol} -> Finance", failure);
        Assert.Contains("#858", failure);
    }

    [Fact]
    public void RowForAMemberTheModuleNowOwns_IsStale()
    {
        WriteProbe("return db.Expenses.Count();", "Cluckwork.Temp.Finance");

        Assert.Contains(Evaluate([Row("Cluckwork.Temp.Finance.Probe.Run")]),
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
            Assert.Single(Evaluate([Row("Cluckwork.Temp.Cli.Verb.Rows")])));
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

    [Fact]
    public void QueriedTables_IncludeOwnedAndDerivedTablesMappedApart()
    {
        var options = new DbContextOptionsBuilder<MappingDb>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            .EnableServiceProviderCaching(false).Options;
        using var context = new MappingDb(options);

        Assert.Equal(["OrderTotals", "Orders", "SpecialShared"], CompatibilityExceptionScanner
            .QueriedTables(context.Model.FindEntityType(typeof(MappedOrder))!).Distinct().Order(StringComparer.Ordinal));
    }

    public sealed class Total { public long Amount { get; set; } }

    public class MappedOrder
    {
        public Guid Id { get; set; }
        public Total Paid { get; set; } = new();
        public Total Due { get; set; } = new();
    }

    public sealed class SpecialOrder : MappedOrder { public string Note { get; set; } = ""; }

    private sealed class MappingDb(DbContextOptions<MappingDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<MappedOrder>().UseTptMappingStrategy().ToTable("Orders");
            builder.Entity<MappedOrder>().OwnsOne(o => o.Paid, m => m.ToTable("OrderTotals"));
            builder.Entity<MappedOrder>().OwnsOne(o => o.Due);
            builder.Entity<SpecialOrder>().ToTable("SpecialShared");
        }
    }
}
