using System.Xml.Linq;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

// #850 (epic #514 slice 8) — every DbSet read of a contracted module's entity from outside that
// module is either allowed structurally or registered under `compatibilityExceptions`.

public sealed record DbSetRead(string Symbol, string Reaches, string Allowance, string File, int Line);

public sealed record CompatibilityExceptionReport(
    IReadOnlyList<DbSetRead> Reads,
    IReadOnlyList<DbSetRead> Undeclared,
    IReadOnlyList<CompatibilityException> Stale,
    IReadOnlyList<string> CompileErrors,
    IReadOnlyList<string> RegistryErrors,
    int CompiledFileCount);

public static class CompatibilityExceptionScanner
{
    // The project whose sources are compiled for a semantic walk. AppDbContext lives here.
    internal const string SemanticProject = "Cluckwork.Infrastructure";

    // Below the 156 files src/Cluckwork.Infrastructure held on 2026-10-02.
    internal const int RealTreeFileFloor = 125;

    internal const string Registered = "registered";
    internal const string OwnModule = "own module";
    internal const string DeclaredEdge = "declared edge";
    internal const string ModulePort = "implements a module port";
    internal const string DbSetDeclaration = "DbSet declaration";

    private const string DbSetDefinition = "Microsoft.EntityFrameworkCore.DbSet<TEntity>";

    // Microsoft.NET.Sdk's implicit usings, which the compiled project enables.
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly SymbolDisplayFormat TypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    public static CompatibilityExceptionReport Scan(string srcRoot, string ledgerPath)
    {
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)!;
        var ledger = ModuleLedger.Load(ledgerPath);
        var registryErrors = new List<string>(ledger.RegistryErrors);
        var index = ModuleLedgerScanner.BuildNamespaceIndex(ledger, registryErrors);
        var contracted = ledger.Owners.Where(o => o.Contract.Count > 0).Select(o => o.Name)
            .ToHashSet(StringComparer.Ordinal);
        ValidateRows(ledger, contracted, registryErrors);

        string? OwnerOf(INamespaceSymbol ns) =>
            ModuleLedgerScanner.Resolve(index, ns.ToDisplayString(), declared: true)?.Owner;

        var files = GuardScanner.EnumerateSourceFiles(Path.Combine(srcFull, SemanticProject));
        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), ModuleLedgerScanner.ParseOptions, f))
            .Append(CSharpSyntaxTree.ParseText(ImplicitUsings, ModuleLedgerScanner.ParseOptions))
            .ToList();
        var compilation = CSharpCompilation.Create(SemanticProject, trees, References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{Relative(repoRoot, d.Location.SourceTree?.FilePath ?? "")}:{Line(d.Location)}: {d.Id} {d.GetMessage()}")
            .ToList();

        var reads = new List<DbSetRead>();
        var guardedProperties = new Dictionary<string, string>(StringComparer.Ordinal);
        var guardedEntities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var expression in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                if (expression is not (MemberAccessExpressionSyntax or InvocationExpressionSyntax or IdentifierNameSyntax)
                    || model.GetTypeInfo(expression).Type is not INamedTypeSymbol { TypeArguments: [INamedTypeSymbol entity] } type
                    || type.OriginalDefinition.ToDisplayString() != DbSetDefinition
                    || OwnerOf(entity.ContainingNamespace) is not { } module
                    || !contracted.Contains(module))
                {
                    continue;
                }

                guardedEntities[entity.Name] = module;
                var member = EnclosingMember(model, expression);
                if (member is IPropertySymbol property && property.Type.OriginalDefinition.ToDisplayString() == DbSetDefinition)
                {
                    guardedProperties[property.Name] = module;
                }

                var symbol = member is INamedTypeSymbol named ? Key(named) : $"{Key(member.ContainingType)}.{member.Name}";
                reads.Add(new DbSetRead(symbol, module, Allowance(member, module), Relative(repoRoot, tree.FilePath), Line(expression.GetLocation())));
            }
        }

        reads.AddRange(UnwalkedReads(srcFull, repoRoot, guardedProperties, guardedEntities));

        // One row per (member, module); the first site is the one reported.
        var distinct = reads
            .GroupBy(r => (r.Symbol, r.Reaches))
            .Select(g => g.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).First())
            .OrderBy(r => r.Symbol, StringComparer.Ordinal).ThenBy(r => r.Reaches, StringComparer.Ordinal)
            .ToList();
        var declared = ledger.CompatibilityExceptions.Select(e => (e.Symbol, e.Reaches)).ToHashSet();
        var undeclared = distinct.Where(r => r.Allowance == Registered && !declared.Contains((r.Symbol, r.Reaches))).ToList();
        var registered = distinct.Where(r => r.Allowance == Registered).Select(r => (r.Symbol, r.Reaches)).ToHashSet();
        var stale = ledger.CompatibilityExceptions.Where(e => !registered.Contains((e.Symbol, e.Reaches))).ToList();

        return new CompatibilityExceptionReport(distinct, undeclared, stale, compileErrors, registryErrors, files.Count);

        string Allowance(ISymbol member, string module)
        {
            var type = member as INamedTypeSymbol ?? member.ContainingType;
            var outermost = type;
            while (outermost.ContainingType is { } parent)
            {
                outermost = parent;
            }

            var owner = OwnerOf(outermost.ContainingNamespace);
            if (owner == module)
            {
                return OwnModule;
            }

            var key = Key(outermost);
            if (ledger.Edges.Any(e => e.From == owner && e.To == module && e.Symbols.Contains(key, StringComparer.Ordinal)))
            {
                return DeclaredEdge;
            }

            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (current.AllInterfaces.Any(i => OwnerOf(i.ContainingNamespace) == module))
                {
                    return ModulePort;
                }
            }

            return member is IPropertySymbol property
                && property.Type.OriginalDefinition.ToDisplayString() == DbSetDefinition
                && DerivesFromDbContext(type)
                ? DbSetDeclaration
                : Registered;
        }
    }

    public static IReadOnlyList<string> Evaluate(CompatibilityExceptionReport report)
    {
        var failures = new List<string>();
        failures.AddRange(report.RegistryErrors.Select(e => $"registry: {e}"));
        failures.AddRange(report.CompileErrors.Select(e =>
            $"compile: {e} — the semantic walk cannot bind {SemanticProject}, so it would miss reads"));
        failures.AddRange(report.Undeclared.Select(r =>
            $"undeclared compatibility exception {r.Symbol} -> {r.Reaches} at {r.File}:{r.Line}. Read through the " +
            $"module's contract, or add this row to compatibilityExceptions and fill in owner, reason and deleteWhen:\n" +
            $"{{ \"symbol\": \"{r.Symbol}\", \"reaches\": \"{r.Reaches}\", \"owner\": \"\", \"reason\": \"\", \"deleteWhen\": \"#\" }}"));
        failures.AddRange(report.Stale.Select(e =>
            $"stale compatibility exception {e.Symbol} -> {e.Reaches}: no member by that key reads {e.Reaches}'s " +
            $"tables any more, so delete the row (its trigger was {e.DeleteWhen})"));
        return failures;
    }

    private static void ValidateRows(ModuleLedger ledger, HashSet<string> contracted, List<string> errors)
    {
        var owners = ledger.Owners.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var row in ledger.CompatibilityExceptions)
        {
            if (!string.IsNullOrWhiteSpace(row.Reaches) && !contracted.Contains(row.Reaches))
            {
                errors.Add($"compatibilityExceptions row '{row.Symbol}' reaches '{row.Reaches}', which declares no " +
                    "contract — only a contracted module's tables are guarded");
            }

            if (!string.IsNullOrWhiteSpace(row.Owner) && !owners.Contains(row.Owner))
            {
                errors.Add($"compatibilityExceptions row '{row.Symbol}' has unknown owner '{row.Owner}'");
            }
        }
    }

    // Projects that reference the semantic project cannot be compiled here (their packages are not
    // on this test's path), so their reads are found by name: a member access named after a guarded
    // DbSet property, or Set<T>() of a guarded entity. Key by namespace, type and member.
    private static IEnumerable<DbSetRead> UnwalkedReads(string srcFull, string repoRoot,
        Dictionary<string, string> properties, Dictionary<string, string> entities)
    {
        foreach (var project in ProjectsReferencing(srcFull, SemanticProject))
        {
            foreach (var file in GuardScanner.EnumerateSourceFiles(Path.Combine(srcFull, project)))
            {
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), ModuleLedgerScanner.ParseOptions).GetRoot();
                foreach (var node in root.DescendantNodes())
                {
                    var module = node switch
                    {
                        MemberAccessExpressionSyntax access => properties.GetValueOrDefault(access.Name.Identifier.ValueText),
                        GenericNameSyntax { Identifier.ValueText: "Set", TypeArgumentList.Arguments: [var argument] } =>
                            entities.GetValueOrDefault(ModuleLedgerScanner.DottedText(argument)?.Split('.')[^1] ?? ""),
                        _ => null,
                    };
                    if (module is not null)
                    {
                        yield return new DbSetRead(SyntacticKey(node), module, Registered, Relative(repoRoot, file), Line(node.GetLocation()));
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ProjectsReferencing(string srcFull, string target)
    {
        var references = Directory.EnumerateDirectories(srcFull)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(p => File.Exists(Path.Combine(srcFull, p, p + ".csproj")))
            .ToDictionary(p => p, p => XDocument.Load(Path.Combine(srcFull, p, p + ".csproj"))
                .Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")?.Value.Replace('\\', '/') ?? ""))
                .ToList(), StringComparer.Ordinal);

        bool Reaches(string project, HashSet<string> seen) =>
            seen.Add(project) && references.TryGetValue(project, out var direct)
            && direct.Any(d => d == target || Reaches(d, seen));

        return references.Keys.Where(p => Reaches(p, [])).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private static ISymbol EnclosingMember(SemanticModel model, SyntaxNode node)
    {
        var symbol = model.GetEnclosingSymbol(node.SpanStart)!;
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }

        return symbol is IMethodSymbol { AssociatedSymbol: { } associated } ? associated : symbol;
    }

    private static bool DerivesFromDbContext(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbContext")
            {
                return true;
            }
        }

        return false;
    }

    private static string SyntacticKey(SyntaxNode node)
    {
        var types = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText);
        var member = node.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax and not BaseTypeDeclarationSyntax) switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier.ValueText,
            _ => null,
        };
        var type = string.Join(".", types.Prepend(ModuleLedgerScanner.NamespaceOf(node, "<global>")));
        return member is null ? type : $"{type}.{member}";
    }

    private static IEnumerable<MetadataReference> References()
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return platform.Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            .Where(p => Path.GetFileNameWithoutExtension(p) != SemanticProject)
            .Distinct(StringComparer.Ordinal)
            .Select(p => MetadataReference.CreateFromFile(p));
    }

    private static string Key(INamedTypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
}
