using System.Text.RegularExpressions;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Eggs;

// #853: every FOR UPDATE over EggLots must end ORDER BY "ProductionDate", "Id".
// EggLotLockOrderTests proves the order behaviourally, but the sale void runs
// after the confirm rewrote both lots in canonical order, so its bitmap heap
// scan already returns that order and a dropped Id tie-breaker there changes
// nothing a test can observe. This pins the text of every such statement.
public sealed partial class EggLotLockSqlTests
{
    [Fact]
    public void EveryEggLotsForUpdate_OrdersByProductionDateThenId()
    {
        var src = Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src");

        var sites = new List<(string Where, string Sql)>();
        foreach (var file in GuardScanner.EnumerateSourceFiles(src))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                var text = node switch
                {
                    InterpolatedStringExpressionSyntax s => string.Concat(s.Contents.Select(c =>
                        c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "{}")),
                    LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
                    _ => null,
                };
                if (text is null) continue;
                var sql = Whitespace().Replace(SqlComment().Replace(text, ""), " ").Trim();
                if (sql.Contains("\"EggLots\"") && sql.Contains("FOR UPDATE"))
                    sites.Add(($"{Path.GetRelativePath(src, file)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}", sql));
            }
        }

        Assert.True(sites.Count >= 3, $"found {sites.Count} FOR UPDATE statements over EggLots, expected at least 3");
        var unordered = sites.Where(s => !s.Sql.EndsWith("ORDER BY \"ProductionDate\", \"Id\" FOR UPDATE", StringComparison.Ordinal))
            .Select(s => $"{s.Where}: {s.Sql}").ToList();
        Assert.True(unordered.Count == 0,
            "FOR UPDATE over EggLots must end ORDER BY \"ProductionDate\", \"Id\":\n" + string.Join("\n", unordered));
    }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex SqlComment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
