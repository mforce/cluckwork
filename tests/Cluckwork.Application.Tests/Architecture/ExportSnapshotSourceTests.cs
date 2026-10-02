using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

// #269 — BeginConsistentReadAsync swaps ExportQueries.activeDb to the
// REPEATABLE READ snapshot context, so a dataset is inside the snapshot only
// if its query starts from activeDb. ExportTests proves the swap against real
// Postgres for one dataset; this proves every GetDataset arm reads through
// activeDb and no other AppDbContext.
public sealed class ExportSnapshotSourceTests
{
    private static string ExportQueriesPath => Path.Combine(
        GuardScanner.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repo root not found"),
        "src", "Cluckwork.Infrastructure", "Insights", "ExportQueries.cs");

    [Fact]
    public void EveryDatasetArm_QueriesOnlyTheActiveContext()
    {
        var violations = FindViolations(File.ReadAllText(ExportQueriesPath));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Theory]
    [InlineData("activeDb.Customers", "requestDb.Customers")]
    [InlineData("activeDb.Customers", "db.Customers")]
    [InlineData("activeDb.AuditEvents", "this.requestDb.AuditEvents")]
    [InlineData("\"payments\" =>", "\"payments-renamed\" =>")]
    public void OneArmBypassingTheActiveContext_IsReported(string original, string mutated)
    {
        var source = File.ReadAllText(ExportQueriesPath);
        Assert.Contains(original, source, StringComparison.Ordinal);
        Assert.NotEmpty(FindViolations(source.Replace(original, mutated, StringComparison.Ordinal)));
    }

    private static List<string> FindViolations(string source)
    {
        var type = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
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
        if (!armNames.SequenceEqual(datasetNames))
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
