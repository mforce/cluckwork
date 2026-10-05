using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

// #269 — BeginConsistentReadAsync swaps ExportQueries.activeDb to the
// REPEATABLE READ snapshot context. This is a syntax check of one convention:
// each GetDataset arm names the activeDb field directly, names no other
// AppDbContext field or parameter, and the arms cover DatasetNames. It does
// not resolve identifiers, so indirection (a local shadowing activeDb, a
// helper that ignores its argument, a ternary) passes it; review and the
// behavioural tests in ExportTests own those. Renaming activeDb fails it.
public sealed class ExportSnapshotSourceTests
{
    private static string ExportQueriesPath => Path.Combine(
        GuardScanner.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repo root not found"),
        "src", "Cluckwork.Infrastructure", "Modules", "Insights", "Repositories", "ExportQueries.cs");

    [Fact]
    public void EveryDatasetArm_NamesTheSnapshotFieldDirectly()
    {
        var violations = FindViolations(File.ReadAllText(ExportQueriesPath));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Theory]
    [InlineData("activeDb.Customers", "requestDb.Customers")]
    [InlineData("activeDb.Customers", "db.Customers")]
    [InlineData("activeDb.AuditEvents", "this.requestDb.AuditEvents")]
    [InlineData("\"payments\" =>", "\"payments-renamed\" =>")]
    public void OneArmNamingAnotherContext_IsReported(string original, string mutated)
    {
        var source = File.ReadAllText(ExportQueriesPath);
        Assert.Contains(original, source, StringComparison.Ordinal);
        Assert.NotEmpty(FindViolations(source.Replace(original, mutated, StringComparison.Ordinal)));
    }

    [Fact]
    public void ArmInAnActiveFrameworkConditional_NamingAnotherContextIsReported()
    {
        var violations = FindViolations("""
            class ExportQueries(AppDbContext db)
            {
                private AppDbContext requestDb = db;
                private AppDbContext activeDb = db;
                private static string[] DatasetNames = ["customers"];
                public object GetDataset(string dataset) => dataset switch
                {
            #if NET10_0
                    "customers" => requestDb.Customers,
            #else
                    "customers" => activeDb.Customers,
            #endif
                };
            }
            """);

        Assert.Equal(["\"customers\" reads through [requestDb], not only activeDb"], violations);
    }

    private static List<string> FindViolations(string source)
    {
        var type = CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "ExportQueries");

        var contexts = type.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Type.ToString() == "AppDbContext")
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.ValueText))
            .Concat(type.ParameterList!.Parameters
                .Where(p => p.Type?.ToString() == "AppDbContext")
                .Select(p => p.Identifier.ValueText))
            .ToHashSet();
        Assert.Contains("activeDb", contexts);
        Assert.True(contexts.Count >= 3, $"expected activeDb, requestDb and db; found {string.Join(", ", contexts)}");

        var datasetNames = type.Members.OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "DatasetNames"))
            .DescendantNodes().OfType<LiteralExpressionSyntax>().Select(l => l.Token.ValueText).ToList();

        var arms = type.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "GetDataset")
            .DescendantNodes().OfType<SwitchExpressionSyntax>().Single().Arms
            .Where(a => a.Pattern is ConstantPatternSyntax)
            .ToList();

        var violations = new List<string>();
        var armNames = arms.Select(a => ((LiteralExpressionSyntax)((ConstantPatternSyntax)a.Pattern).Expression).Token.ValueText).ToList();
        if (!armNames.Order().SequenceEqual(datasetNames.Order()))
            violations.Add($"GetDataset arms [{string.Join(", ", armNames)}] differ from DatasetNames [{string.Join(", ", datasetNames)}]");

        foreach (var (arm, name) in arms.Zip(armNames))
        {
            var used = arm.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Select(i => i.Identifier.ValueText).Where(contexts.Contains).ToHashSet();
            if (!used.SetEquals(["activeDb"]))
                violations.Add($"\"{name}\" reads through [{string.Join(", ", used)}], not only activeDb");
        }
        return violations;
    }
}
