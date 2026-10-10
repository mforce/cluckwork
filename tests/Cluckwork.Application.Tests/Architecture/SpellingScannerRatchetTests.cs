using Cluckwork.Application.Tests.Documentation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

// A test type that parses C# but never builds a CSharpCompilation resolves names by spelling, and reviewers then
// find one bypass per round: aliases, generic arity, nested types, `global using`, `#if !DEBUG` (#1010, #1013,
// #1033, #1056; 30 PRs in all, #1184). New C# invariants go in Cluckwork.Analyzers, where the compiler binds every
// name. The types already written this way are listed by fully qualified name and may change freely; the list only
// shrinks. Detection binds symbols itself, so an alias or `using static` of CSharpSyntaxTree is still a parse call.
public sealed class SpellingScannerRatchetTests
{
    // Fails an enumeration that silently finds nothing; 387 tracked test files on 2026-10-10.
    private const int ScannedFileFloor = 300;

    // Type → what it guards and why it may stay until someone next changes it.
    private static readonly Dictionary<string, string> Legacy = new(StringComparer.Ordinal)
    {
        ["Cluckwork.Api.IntegrationTests.TrackedMutationReadTests"] =
            "#546/#1057 tracked-read guard; matches repository members and DbSet entity names by spelling. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.Architecture.AdapterReachScanner"] =
            "#846 adapter reach ratchet; resolves parameter types by name. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.Architecture.AdapterTierScanner"] =
            "#843 adapter tiers; matches [McpServerToolType] and MapMcp by name. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.Architecture.ExportSnapshotSourceTests"] =
            "#1025 export snapshot pin; matches the activeDb identifier by name. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.Architecture.NamespaceFolderAgreementTests"] =
            "Namespace declarations against folders; the declaration's text is the subject, so syntax is enough.",
        ["Cluckwork.Application.Tests.Architecture.SourcePreprocessorTests"] =
            "#1056 tests ModuleLedgerScanner.ParseOptions itself; which #if branches parse is the subject.",
        ["Cluckwork.Application.Tests.Eggs.EggLotLockSqlTests"] =
            "#1028 pins lock SQL string literals; literal text is the subject. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.Sales.TransactionDelegateShapeTests"] =
            "#751 add-item transaction shape; matches call names inside the delegate. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.TenantBypass.FarmDirectoryCallerTests"] =
            "#1043 IFarmDirectory caller allow-list; matches callers by name. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.TenantBypass.FindBySlugCallerTests"] =
            "#1061 FindBySlugAsync caller allow-list; matches callers by name. Legacy, predates #1184.",
        ["Cluckwork.Application.Tests.TenantBypass.GuardScanner"] =
            "#584 tenant-bypass scanner; matches banned methods by name, keyed by #632 token hash. Legacy, predates #1184.",
    };

    [Fact]
    public void NoNewTestType_ParsesCSharpWithoutBindingSymbols()
    {
        var root = TenancyDocsFreshnessTests.RepoRoot();
        var files = TenancyDocsFreshnessTests.TrackedFiles(root)
            .Where(p => p.StartsWith("tests/", StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal))
            .Select(p => (Project: p.Split('/')[1], Path: p, Source: File.ReadAllText(Path.Combine(root, p))))
            .ToList();
        Assert.True(files.Count >= ScannedFileFloor,
            $"Scanned {files.Count} test files, below the floor of {ScannedFileFloor}; the enumeration is excluding too much.");

        var found = SpellingOnlyParsers(files);

        var failures = new List<string>();
        foreach (var (type, path) in found.Where(f => !Legacy.ContainsKey(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            failures.Add($"{type} ({path}) parses C# without a CSharpCompilation, so it matches names by spelling. " +
                "Write the invariant as a Cluckwork.Analyzers diagnostic (see src/Cluckwork.Analyzers/ModuleEdgeAnalyzer.cs, " +
                "CW1004), or bind symbols through CSharpCompilation and its SemanticModel.");
        }
        foreach (var type in Legacy.Keys.Where(k => !found.ContainsKey(k)).Order(StringComparer.Ordinal))
            failures.Add($"{type}: listed, but no longer parses C# by spelling (moved, deleted or now binds symbols). Delete its row.");

        Assert.True(failures.Count == 0, "Spelling-based C# scanners:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void Detection_BindsAliasesAndStaticImports_AndExemptsCompilations()
    {
        const string header = "using Microsoft.CodeAnalysis.CSharp;\n";
        var found = SpellingOnlyParsers(
        [
            ("P", "Direct.cs", header + "class Direct { object M() => CSharpSyntaxTree.ParseText(\"\"); }"),
            ("P", "Alias.cs", "using T = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree;\nclass Alias { object M() => T.ParseText(\"\"); }"),
            ("P", "Static.cs", "using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;\nclass Static { object M() => ParseCompilationUnit(\"\"); }"),
            ("P", "Nested.cs", header + "namespace N { class Outer { class Inner { System.Func<string, object> F = s => CSharpSyntaxTree.ParseText(s); } } }"),
            ("P", "Bound.cs", header + "class Bound { object M() => CSharpCompilation.Create(\"x\", [CSharpSyntaxTree.ParseText(\"\")]); }"),
            ("P", "Unrelated.cs", "class Unrelated { int ParseText(string s) => s.Length; int M() => ParseText(\"\"); }"),
        ]);

        Assert.Equal(["Alias", "Direct", "N.Outer", "Static"], found.Keys.Order(StringComparer.Ordinal));
    }

    // Keyed by the outermost type, merged across partial declarations. A parse call counts when its bound symbol is a
    // Parse* or Create method on CSharpSyntaxTree or SyntaxFactory; a method name cannot be aliased, so the name
    // prefilter loses nothing short of reflection. The type is exempt when any of its code binds CSharpCompilation.
    internal static Dictionary<string, string> SpellingOnlyParsers(IEnumerable<(string Project, string Path, string Source)> files)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in files.GroupBy(f => f.Project))
        {
            var trees = project.Select(f => CSharpSyntaxTree.ParseText(f.Source, ModuleLedgerScanner.ParseOptions, f.Path)).ToList();
            var compilation = CompatibilityExceptionScanner.Compile(project.Key, trees,
                CompatibilityExceptionScanner.ImplicitUsings, CompatibilityExceptionScanner.References(project.Key));

            var parsers = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            var bound = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var text = name.Identifier.ValueText;
                    if (!text.StartsWith("Parse", StringComparison.Ordinal) && text is not ("Create" or "CSharpCompilation"))
                        continue;
                    var info = model.GetSymbolInfo(name);
                    var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (OutermostType(model, name) is not { } owner)
                        continue;
                    if (IsCompilation(symbol))
                        bound.Add(owner);
                    else if (symbol is IMethodSymbol method && IsParse(method))
                        parsers.TryAdd(owner, tree.FilePath);
                }
            }

            foreach (var (owner, path) in parsers.Where(p => !bound.Contains(p.Key)))
                found[owner.ToDisplayString()] = path;
        }

        return found;
    }

    private static bool IsParse(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() is "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree" or "Microsoft.CodeAnalysis.CSharp.SyntaxFactory"
        && (method.Name.StartsWith("Parse", StringComparison.Ordinal) || method.Name == "Create");

    private static bool IsCompilation(ISymbol? symbol) =>
        (symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.ToDisplayString() == "Microsoft.CodeAnalysis.CSharp.CSharpCompilation";

    private static INamedTypeSymbol? OutermostType(SemanticModel model, SyntaxNode node)
    {
        var type = model.GetEnclosingSymbol(node.SpanStart) is { } enclosing
            ? enclosing as INamedTypeSymbol ?? enclosing.ContainingType
            : null;
        while (type?.ContainingType is { } outer)
            type = outer;
        return type;
    }
}
