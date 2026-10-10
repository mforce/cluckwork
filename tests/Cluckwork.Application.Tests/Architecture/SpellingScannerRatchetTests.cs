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

    // Type → what it guards.
    private static readonly Dictionary<string, string> Legacy = new(StringComparer.Ordinal)
    {
        ["Cluckwork.Api.IntegrationTests.TrackedMutationReadTests"] =
            "#546/#1057 tracked-read guard; matches repository members and DbSet entity names by spelling.",
        ["Cluckwork.Application.Tests.Architecture.AdapterReachScanner"] =
            "#846 adapter reach ratchet; resolves parameter types by name.",
        ["Cluckwork.Application.Tests.Architecture.AdapterTierScanner"] =
            "#843 adapter tiers; matches [McpServerToolType] and MapMcp by name.",
        ["Cluckwork.Application.Tests.Architecture.ExportSnapshotSourceTests"] =
            "#1025 export snapshot pin; matches the activeDb identifier by name.",
        ["Cluckwork.Application.Tests.Architecture.NamespaceFolderAgreementTests"] =
            "Namespace declarations against folders; the declaration's text is the subject, so syntax is enough.",
        ["Cluckwork.Application.Tests.Architecture.SourcePreprocessorTests"] =
            "#1056 tests ModuleLedgerScanner.ParseOptions itself; which #if branches parse is the subject.",
        ["Cluckwork.Application.Tests.Eggs.EggLotLockSqlTests"] =
            "#1028 pins lock SQL string literals; literal text is the subject.",
        ["Cluckwork.Application.Tests.Sales.TransactionDelegateShapeTests"] =
            "#751 add-item transaction shape; matches call names inside the delegate.",
        ["Cluckwork.Application.Tests.TenantBypass.FarmDirectoryCallerTests"] =
            "#1043 IFarmDirectory caller allow-list; matches callers by name.",
        ["Cluckwork.Application.Tests.TenantBypass.FindBySlugCallerTests"] =
            "#1061 FindBySlugAsync caller allow-list; matches callers by name.",
        ["Cluckwork.Application.Tests.TenantBypass.GuardScanner"] =
            "#584 tenant-bypass scanner; matches banned methods by name, keyed by #632 token hash.",
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

        var found = SpellingOnlyParsers(files, project => ProjectUsings(root, project));

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
    public void Detection_BindsAliasesAndStaticImports_AndExemptsSemanticModels()
    {
        const string header = "using Microsoft.CodeAnalysis.CSharp;\n";
        var found = SpellingOnlyParsers(
        [
            ("P", "Direct.cs", header + "class Direct { object M() => CSharpSyntaxTree.ParseText(\"\"); }"),
            ("P", "Alias.cs", "using T = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree;\nclass Alias { object M() => T.ParseText(\"\"); }"),
            ("P", "Static.cs", "using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;\nclass Static { object M() => ParseCompilationUnit(\"\"); }"),
            ("P", "Nested.cs", header + "namespace N { class Outer { class Inner { System.Func<string, object> F = s => CSharpSyntaxTree.ParseText(s); } } }"),
            ("P", "Bound.cs", header + "class Bound { object M() => CSharpCompilation.Create(\"x\", [CSharpSyntaxTree.ParseText(\"\")]); }"),
            ("P", "Helper.cs", header + "class Helper { object M(Microsoft.CodeAnalysis.Compilation c) => c.GetSemanticModel(CSharpSyntaxTree.ParseText(\"\")); }"),
            ("P", "Unrelated.cs", "class Unrelated { int ParseText(string s) => s.Length; int M() => ParseText(\"\"); }"),
            ("P", "HelperOwner.cs", header + "static class HelperOwner { internal static Microsoft.CodeAnalysis.SyntaxTree Tree(string s) => CSharpSyntaxTree.ParseText(s); " +
                "internal static Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax Root(string s) => SyntaxFactory.ParseCompilationUnit(s); " +
                "internal static object Bind(Microsoft.CodeAnalysis.Compilation c, Microsoft.CodeAnalysis.SyntaxTree t) => c.GetSemanticModel(t); }"),
            ("P", "Reuse.cs", "class Reuse { object M() => HelperOwner.Tree(\"\").GetRoot(); }"),
            ("P", "ReuseNode.cs", "class ReuseNode { object M() => HelperOwner.Root(\"\"); }"),
            ("P", "Group.cs", "class Group { object M(string[] s) => s.Select(HelperOwner.Tree).ToList(); }"),
            ("P", "Unresolved.cs", "class Unresolved { object M() => CSharpSyntaxTree.ParseText(\"\"); }"),
            ("Q", "ProjectUsing.cs", "class ProjectUsing { object M() => SyntaxFactory.ParseExpression(\"x\"); }"),
        ], project => project == "Q" ? "global using Microsoft.CodeAnalysis.CSharp;" : "");

        Assert.Equal(["Alias", "Direct", "Group", "N.Outer", "ProjectUsing", "Reuse", "ReuseNode", "Static", "Unresolved"], found.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MsBuildUsingItems_BecomeGlobalUsings()
    {
        var usings = MsBuildGlobalUsings(System.Xml.Linq.XDocument.Parse("""
            <Project><ItemGroup>
              <Using Include="A.B" />
              <Using Include="A.C" Static="true" />
              <Using Include="A.D" Alias="E" />
            </ItemGroup></Project>
            """));

        Assert.Equal(["global using A.B;", "global using static A.C;", "global using E = A.D;"], usings);
    }

    // Keyed by the outermost type, merged across partial declarations. Every invocation and method-group argument is
    // bound; a parse call is one whose symbol IsParser, or an unbound call spelled like a Roslyn parse method. The type is exempt when any of its code creates a CSharpCompilation or
    // asks a compilation for its SemanticModel, which also covers a compilation built by a shared helper.
    internal static Dictionary<string, string> SpellingOnlyParsers(
        IEnumerable<(string Project, string Path, string Source)> files, Func<string, string> projectUsings)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in files.GroupBy(f => f.Project))
        {
            var trees = project.Select(f => CSharpSyntaxTree.ParseText(f.Source, ModuleLedgerScanner.ParseOptions, f.Path)).ToList();
            var compilation = CompatibilityExceptionScanner.Compile(project.Key, trees,
                CompatibilityExceptionScanner.ImplicitUsings + "\n" + projectUsings(project.Key),
                CompatibilityExceptionScanner.References(project.Key));

            var parsers = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            var bound = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    var call = node switch
                    {
                        InvocationExpressionSyntax invocation => invocation.Expression,
                        ArgumentSyntax { Expression: IdentifierNameSyntax or MemberAccessExpressionSyntax } group => group.Expression,
                        _ => null,
                    };
                    if (call is null)
                        continue;
                    var info = model.GetSymbolInfo(call);
                    var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    var text = (call as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText ?? (call as SimpleNameSyntax)?.Identifier.ValueText;
                    if ((symbol is IMethodSymbol method ? IsParser(method) : symbol is null && text is not null && UnresolvedParse.Contains(text))
                        && OutermostType(model, node) is { } owner)
                        parsers.TryAdd(owner, tree.FilePath);
                }
                foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    if (name.Identifier.ValueText is not ("Create" or "CSharpCompilation" or "GetSemanticModel" or "SemanticModel"))
                        continue;
                    var info = model.GetSymbolInfo(name);
                    if (BindsSymbols(info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) && OutermostType(model, name) is { } owner)
                        bound.Add(owner);
                }
            }

            foreach (var (owner, path) in parsers.Where(p => !bound.Contains(p.Key)))
                found[owner.ToDisplayString()] = path;
        }

        return found;
    }

    // The SDK turns <Using> items into generated global usings, which tracked source does not contain.
    internal static IEnumerable<string> MsBuildGlobalUsings(System.Xml.Linq.XDocument project) =>
        project.Descendants("Using").Where(u => u.Attribute("Include") is not null).Select(u =>
            (string?)u.Attribute("Alias") is { } alias ? $"global using {alias} = {(string)u.Attribute("Include")!};"
            : (string?)u.Attribute("Static") == "true" ? $"global using static {(string)u.Attribute("Include")!};"
            : $"global using {(string)u.Attribute("Include")!};");

    private static string ProjectUsings(string root, string project) => string.Join("\n",
        new[] { "Directory.Build.props", "tests/Directory.Build.props", $"tests/{project}/{project}.csproj" }
            .Select(p => Path.Combine(root, p)).Where(File.Exists)
            .SelectMany(p => MsBuildGlobalUsings(System.Xml.Linq.XDocument.Load(p))));


    // Fail closed: a parser call that does not bind (a missing import or reference) still counts as one.
    private static readonly HashSet<string> UnresolvedParse = new(StringComparer.Ordinal)
        { "ParseText", "ParseSyntaxTree", "ParseCompilationUnit" };

    // A Roslyn parse factory, or a helper of ours that hands back a tree or C# node: its caller scans syntax too.
    private static bool IsParser(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() is "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree" or "Microsoft.CodeAnalysis.CSharp.SyntaxFactory"
            ? method.Name.StartsWith("Parse", StringComparison.Ordinal) || method.Name == "Create"
            : !method.ContainingAssembly.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
              && DerivesFrom(method.ReturnType, "Microsoft.CodeAnalysis.SyntaxTree", "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode");

    private static bool DerivesFrom(ITypeSymbol? type, params string[] bases)
    {
        for (; type is not null; type = type.BaseType)
            if (bases.Contains(type.ToDisplayString()))
                return true;
        return false;
    }

    private static bool BindsSymbols(ISymbol? symbol) =>
        (symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.ToDisplayString() is
            "Microsoft.CodeAnalysis.CSharp.CSharpCompilation" or "Microsoft.CodeAnalysis.Compilation" or "Microsoft.CodeAnalysis.SemanticModel";

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
