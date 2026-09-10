namespace Cluckwork.Application.Tests.Sales;

using System.Linq;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// #743 — a syntax guard, not a behavioural one. The hazard: a handler that
// calls unitOfWork.SaveChangesAsync(...) INSIDE its ExecuteInTransactionAsync
// delegate (so an audit row can carry an EF-assigned id — AddOrderItemHandler
// does this on purpose, see its #722/#743 comment) must not exit that
// delegate — `return false` or a throw — BELOW that save. A rollback after
// the inner save leaves the just-saved entities tracked as Unchanged rather
// than Added/Modified, so a later flush on the same scoped AppDbContext does
// not re-write them; it silently drops them. Today the only `return false` in
// AddOrderItemHandler's delegate sits above the save, so this is not a live
// bug — it is a shape the compiler happily accepts the moment someone adds an
// exit below the save (verified: inserting one there still builds clean).
//
// This is necessarily SYNTACTIC, not behavioural: the hazard does not exist
// on the current tree, so no runtime test can exercise it. Its stated limit:
// it catches a syntactic `return false;` or `throw` statement below the save.
// It does NOT catch an exception thrown by a call below the save that isn't a
// `throw` statement in this delegate (e.g. a nested call that itself throws)
// — banning every call below the save would ban the audit write itself. That
// case is real but already covered behaviourally by
// SalesOrderAuditPayloadTests.AddItem_WhenTheAuditWriteFails_RollsBackTheLine,
// which drives the actual rollback-after-save path that exists today (the
// audit write, which is itself after the save).
public sealed class TransactionDelegateShapeTests
{
    private sealed record InScopeDelegate(string File, int Line);

    private static string RepoRoot() =>
        GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");

    private static IReadOnlyList<string> EnumerateSrcFiles(string root)
    {
        var srcRoot = Path.Combine(root, "src");
        var files = new List<string>();

        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj") continue;
                Walk(sub);
            }

            files.AddRange(Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly));
        }

        Walk(srcRoot);
        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    // Descendants of `root`, but stop descending the moment a nested
    // AnonymousFunctionExpressionSyntax is hit — its body belongs to a
    // DIFFERENT delegate and must not be attributed to this one. The nested
    // lambda node itself still comes back from Roslyn (only its children are
    // skipped), which is harmless: callers only look for specific node types.
    private static IEnumerable<SyntaxNode> DescendantsExcludingNestedLambdas(SyntaxNode root) =>
        root.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax);

    private static string RelativePath(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    [Fact]
    public void NoExitBelowTheInnerSaveInAnyExecuteInTransactionDelegate()
    {
        var root = RepoRoot();
        var files = EnumerateSrcFiles(root);

        // A path-filter bug that silently excludes a subtree would otherwise
        // read as "no violations". 300 is comfortably below the ~350+ .cs
        // files under src/ today, leaving headroom for growth.
        Assert.True(files.Count >= 300,
            $"walk saw only {files.Count} .cs files under src/ — the floor is 300. The scanner is not seeing the tree.");

        var parseErrors = new List<string>();
        var violations = new List<string>();
        var siteShapeFailures = new List<string>();
        var inScope = new List<InScopeDelegate>();
        var skipped = new List<InScopeDelegate>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);

            foreach (var diag in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                parseErrors.Add(
                    $"{RelativePath(root, file)}:{diag.Location.GetLineSpan().StartLinePosition.Line + 1}: {diag.Id} {diag.GetMessage()}");
            }

            var root2 = tree.GetCompilationUnitRoot();
            var executeCalls = root2.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(inv => InvokedName(inv) == "ExecuteInTransactionAsync");

            foreach (var call in executeCalls)
            {
                var line = tree.GetLineSpan(call.Span).StartLinePosition.Line + 1;
                var relFile = RelativePath(root, file);

                if (call.ArgumentList.Arguments.Count == 0
                    || call.ArgumentList.Arguments[0].Expression is not AnonymousFunctionExpressionSyntax anon
                    || anon.Block is null)
                {
                    siteShapeFailures.Add(
                        $"{relFile}:{line}: ExecuteInTransactionAsync's first argument is not a lambda with a " +
                        "block body — the guard's assumptions about this call site's shape moved and it must be re-taught, not skipped.");
                    continue;
                }

                var saveCalls = DescendantsExcludingNestedLambdas(anon.Block)
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv => InvokedName(inv) == "SaveChangesAsync")
                    .ToList();

                if (saveCalls.Count == 0)
                {
                    // This delegate does not save inside itself — the hazard
                    // this guard exists for is not present. Not in scope.
                    skipped.Add(new InScopeDelegate(relFile, line));
                    continue;
                }

                inScope.Add(new InScopeDelegate(relFile, line));
                var lastSaveEnd = saveCalls.Max(s => s.Span.End);

                var exitsAfterSave = DescendantsExcludingNestedLambdas(anon.Block)
                    .Where(n => n.SpanStart > lastSaveEnd)
                    .Where(n =>
                        n is ThrowStatementSyntax
                        || (n is ReturnStatementSyntax ret
                            && ret.Expression is LiteralExpressionSyntax lit
                            && lit.IsKind(SyntaxKind.FalseLiteralExpression)));

                foreach (var exit in exitsAfterSave)
                {
                    var exitLine = tree.GetLineSpan(exit.Span).StartLinePosition.Line + 1;
                    violations.Add(
                        $"{relFile}:{exitLine} exits the `ExecuteInTransactionAsync` delegate **after** its inner " +
                        "`SaveChangesAsync`. The rollback undoes the rows but leaves the entities tracked as " +
                        "`Unchanged`, so a later flush on the same context silently drops them (#743, #159). " +
                        "Either move the exit above the save, or add a `DiscardChanges`-style cleanup on the " +
                        "`!committed` branch the way `UpdateFarmSettingsHandler` does.");
                }
            }
        }

        // A file Roslyn cannot read is a hole, not a pass.
        Assert.Empty(parseErrors);
        // The guard's shape assumptions must be re-taught explicitly, never silently skipped.
        Assert.Empty(siteShapeFailures);

        Assert.Empty(violations);

        // Assert the guard has something to guard: if a future refactor moves
        // the save out of every ExecuteInTransactionAsync delegate, THIS is
        // what tells the next reader the guard went vacuous instead of
        // passing forever on an empty set.
        Assert.NotEmpty(inScope);
        Assert.Contains(inScope, d => d.File.EndsWith("AddOrderItemHandler.cs", StringComparison.Ordinal));
    }
}
