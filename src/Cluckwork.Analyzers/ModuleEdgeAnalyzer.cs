using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cluckwork.Analyzers;

// #859: ModuleLedgerScanner's undeclared-edge, stale-row and unowned-namespace rules (#842, semantic since #1071),
// reported by the compiler while editing and building. ModuleLedgerRealTreeTests stays the CI authority; this
// analyzer only reports earlier, so nothing here guards against suppressing it.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleEdgeAnalyzer : DiagnosticAnalyzer
{
    // The compilation that references every other module assembly, so it alone can tell a row naming a type
    // that exists nowhere.
    private const string RootAssembly = "Cluckwork.Api";

    internal static readonly DiagnosticDescriptor MapMissing = new(
        "CW1000", "Module map unavailable",
        "{0} runs the module-edge analyzer but reads no [ModuleOwner] rows from Cluckwork.Domain",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor Undeclared = new(
        "CW1001", "Undeclared cross-module edge",
        "undeclared cross-owner edge {0} -> {1} from {2} (references {3}); {4}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor Stale = new(
        "CW1002", "Stale module edge",
        "stale module edge {0} -> {1} :: {2}; {3}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor Unowned = new(
        "CW1003", "Unowned namespace",
        $"unowned namespace '{{0}}'; every namespace in src/ must be claimed by exactly one owner in {ModuleMap.RulesDirectory}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

    // #1116: a module may name a peer's contract or seam; an adapter only a contract. Structural exemptions only.
    // Info until the cleanup brings tools/architecture/cw1004-census.sh to zero; then it becomes an Error.
    internal static readonly DiagnosticDescriptor NonContract = new(
        "CW1004", "Non-contract peer reach", "{0} '{1}' reaches {2}'s non-contract type '{3}'",
        "Architecture", DiagnosticSeverity.Info, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MapMissing, Undeclared, Stale, Unowned, NonContract];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        // ModuleLedgerScanner reads every src/*.cs file, generated header or not; Walk skips generator output.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(Start);
    }

    private static void Start(CompilationStartAnalysisContext start)
    {
        var compilation = start.Compilation;
        var assembly = compilation.AssemblyName ?? "<global>";
        if (ModuleMap.Read(compilation) is not { } map)
        {
            start.RegisterCompilationEndAction(end => end.ReportDiagnostic(Diagnostic.Create(MapMissing, Location.None, assembly)));
            return;
        }

        var realised = new ConcurrentDictionary<(string From, string To, string Symbol), byte>();
        var declaredHere = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        start.RegisterSemanticModelAction(model => Walk(model, map, assembly, realised, declaredHere));
        start.RegisterCompilationEndAction(end =>
        {
            foreach (var (from, to, symbols) in map.Edges)
            {
                if (symbols.IsEmpty && assembly == RootAssembly)
                {
                    end.ReportDiagnostic(Diagnostic.Create(Stale, Location.None, from, to, "<none>",
                        "the cell lists no symbols, so it excuses nothing and can never go stale; delete it"));
                }

                foreach (var symbol in symbols)
                {
                    var stale = declaredHere.ContainsKey(symbol)
                        ? !realised.ContainsKey((from, to, symbol))
                        : assembly == RootAssembly && !ExistsAnywhere(end.Compilation, symbol);
                    if (stale)
                    {
                        end.ReportDiagnostic(Diagnostic.Create(Stale, Location.None, from, to, symbol,
                            $"no reference in src/ realises this edge; delete the symbol from its ModuleEdge row in {ModuleMap.RulesFile(from)}, or restore the dependency it was written for"));
                    }
                }
            }
        });
    }

    private static bool ExistsAnywhere(Compilation compilation, string symbol)
    {
        // File-scoped symbols are checked only by the compilation holding their file.
        if (symbol.StartsWith("<file>:", StringComparison.Ordinal) || symbol.IndexOf('@') >= 0)
        {
            return true;
        }

        var lt = symbol.IndexOf('<');
        var metadataName = lt < 0 ? symbol : $"{symbol.Substring(0, lt)}`{symbol.Length - lt - 1}";
        return compilation.GetTypeByMetadataName(metadataName) is not null;
    }

    private static void Walk(
        SemanticModelAnalysisContext context,
        ModuleMap map,
        string assembly,
        ConcurrentDictionary<(string, string, string), byte> realised,
        ConcurrentDictionary<string, byte> declaredHere)
    {
        var model = context.SemanticModel;
        var path = model.SyntaxTree.FilePath.Replace('\\', '/');
        // Generator output has a relative hint path, or sits under obj/; ModuleLedgerScanner never reads either.
        var rooted = path.StartsWith("/", StringComparison.Ordinal) || (path.Length > 2 && path[1] == ':');
        if (!rooted || path.Contains("/obj/"))
        {
            return;
        }

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot(context.CancellationToken);
        var attributions = Attribute(context, root, path, assembly, map);
        foreach (var attribution in attributions)
        {
            declaredHere.TryAdd(attribution.Symbol, 0);
        }

        var reported = new HashSet<(string, string, string)>();
        var topLevelProgram = map.IsTopLevelProgram(assembly);
        foreach (var node in root.DescendantNodes(n => n is not UsingDirectiveSyntax))
        {
            var enclosing = node.Ancestors().LastOrDefault(IsTypeDeclaration);
            var from = attributions.FirstOrDefault(a => a.Scope == enclosing) ?? attributions[0];
            var moduleOwner = from.Owner is { } owner && !map.IsPlatform(owner) ? owner : null;
            var adapter = moduleOwner is null
                && ((enclosing is null && topLevelProgram && node.Ancestors().Any(a => a is GlobalStatementSyntax))
                    || (enclosing is not null && map.IsAdapter(from.Namespace, from.Symbol)));
            if (moduleOwner is null && !adapter)
            {
                continue;
            }

            foreach (var symbol in BoundSymbols(model, node))
            {
                var receiverContract = ThroughContractedReceiver(model, node, symbol);
                foreach (var type in ReferencedTypes(symbol))
                {
                    if (!(receiverContract && SymbolEqualityComparer.Default.Equals(type, symbol?.ContainingType)))
                    {
                        CheckContractReach(context, map, model, node, from.Symbol, moduleOwner, type, reported);
                    }
                }
            }

            if (moduleOwner is null)
            {
                continue;
            }

            foreach (var type in BoundSymbols(model, node).SelectMany(ReferencedTypes))
            {
                var outermost = type.OriginalDefinition;
                while (outermost.ContainingType is { } parent)
                {
                    outermost = parent;
                }

                if (outermost.TypeKind == TypeKind.Error || outermost.ContainingNamespace.IsGlobalNamespace
                    || map.Resolve($"{outermost.ContainingNamespace.ToDisplayString()}.{outermost.Name}", declared: false) is not { } to
                    || to.Owner == moduleOwner || map.IsPlatform(to.Owner))
                {
                    continue;
                }

                var key = (moduleOwner, to.Owner, from.Symbol);
                realised.TryAdd(key, 0);
                if (!map.IsDeclared(moduleOwner, to.Owner, from.Symbol) && reported.Add(key))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Undeclared, node.GetLocation(),
                        moduleOwner, to.Owner, from.Symbol, to.Namespace, map.Fix(moduleOwner, to.Owner, from.Symbol)));
                }
            }
        }
    }

    private static void CheckContractReach(
        SemanticModelAnalysisContext context, ModuleMap map, SemanticModel model, SyntaxNode node, string fromSymbol,
        string? moduleOwner, INamedTypeSymbol type, HashSet<(string, string, string)> reported)
    {
        var definition = type.OriginalDefinition;
        var outermost = definition;
        while (outermost.ContainingType is { } parent)
        {
            outermost = parent;
        }

        if (outermost.TypeKind == TypeKind.Error || outermost.ContainingNamespace.IsGlobalNamespace
            || map.Resolve($"{outermost.ContainingNamespace.ToDisplayString()}.{outermost.Name}", declared: false) is not { } to
            || map.IsPlatform(to.Owner) || to.Owner == moduleOwner)
        {
            return;
        }

        var ns = definition.ContainingNamespace.ToDisplayString();
        var nested = definition.ContainingType is not null;
        var name = nested ? definition.ToDisplayString() : $"{ns}.{definition.Name}";
        if (ModuleContracts.OwnerOf(ns, Hidden(definition)) == to.Owner
            || (moduleOwner is not null && !nested && map.IsSeam(to.Owner, name))
            || (IsEntity(definition)
                && ((moduleOwner is not null && map.IsReadModel(moduleOwner)) || InEntityConfiguration(model, node)))
            || InServiceRegistration(model, node))
        {
            return;
        }

        if (reported.Add((fromSymbol, "CW1004", name)))
        {
            context.ReportDiagnostic(Diagnostic.Create(NonContract, node.GetLocation(),
                moduleOwner ?? "adapter", fromSymbol, to.Owner, name));
        }
    }

    // #1116: IInsightsModule inherits IReportQueries; a call through the contract binds to the inherited port's member.
    // The member's declaring type is excused only when the receiver's static type is a contract type deriving it.
    private static bool ThroughContractedReceiver(SemanticModel model, SyntaxNode node, ISymbol? symbol)
    {
        if (symbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol) || symbol.IsStatic
            || symbol.ContainingType is not { } declaring)
        {
            return false;
        }

        // x! and (x) bind to x's symbol, in any nesting order; judge the expression they wrap.
        while (true)
        {
            if (node is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang)
            {
                node = bang.Operand;
            }
            else if (node is ParenthesizedExpressionSyntax parenthesized)
            {
                node = parenthesized.Expression;
            }
            else
            {
                break;
            }
        }

        var receiver = node switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Expression,
            MemberAccessExpressionSyntax access => access.Expression,
            SimpleNameSyntax { Parent: MemberAccessExpressionSyntax access } name when access.Name == name => access.Expression,
            InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax binding } => BoundReceiver(binding),
            MemberBindingExpressionSyntax binding => BoundReceiver(binding),
            SimpleNameSyntax { Parent: MemberBindingExpressionSyntax binding } => BoundReceiver(binding),
            _ => null,
        };
        if (receiver is null || model.GetTypeInfo(receiver).Type is not INamedTypeSymbol type
            || ModuleContracts.OwnerOf(type.ContainingNamespace?.ToDisplayString(), Hidden(type)) is null)
        {
            return false;
        }

        var target = declaring.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target)
            || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target));
    }

    // a?.M(): the receiver of the member binding directly after '?'.
    private static ExpressionSyntax? BoundReceiver(MemberBindingExpressionSyntax binding) =>
        binding.Parent is ConditionalAccessExpressionSyntax access && access.WhenNotNull == binding
            ? access.Expression
            : binding.Parent is InvocationExpressionSyntax { Parent: ConditionalAccessExpressionSyntax call } invocation
                && call.WhenNotNull == invocation ? call.Expression : null;

    // A nested type is hidden when it or a type enclosing it is not public.
    private static bool Hidden(INamedTypeSymbol type)
    {
        for (var current = type; current.ContainingType is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEntity(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == "Cluckwork.Domain.Common.Entity<TId>")
            {
                return true;
            }
        }

        return false;
    }

    // An FK configuration: TableOwnerRealModelTests declares every cross-owner foreign key it wires.
    private static bool InEntityConfiguration(SemanticModel model, SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => model.GetDeclaredSymbol(t) is { } declared
            && declared.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString()
                == "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<TEntity>"));

    // The composition root: an IServiceCollection extension method (#858's per-owner registration files).
    private static bool InServiceRegistration(SemanticModel model, SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().Any(m => model.GetDeclaredSymbol(m) is { IsExtensionMethod: true } method
            && method.Parameters[0].Type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection");

    // ModuleLedgerScanner.ScanFile's attribution: each top-level type is charged to its namespace owner, or to the
    // owner claiming it (#1023); a file without types is charged as a whole.
    private static List<Attribution> Attribute(
        SemanticModelAnalysisContext context, CompilationUnitSyntax root, string path, string assembly, ModuleMap map)
    {
        var attributions = new List<Attribution>();
        foreach (var type in root.DescendantNodes().Where(IsTypeDeclaration).Where(n => !n.Ancestors().Any(IsTypeDeclaration)))
        {
            var declared = NamespaceOf(type, assembly);
            var owner = map.Resolve(declared, declared: true)?.Owner;
            if (owner is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Unowned, IdentifierLocation(type), declared));
            }

            var symbol = IsFileLocal(type) ? $"{declared}.{Identifier(type)}@{Relative(path)}" : $"{declared}.{Identifier(type)}";
            attributions.Add(new Attribution(type, symbol, map.Claimant(symbol) ?? owner, declared));
        }

        if (attributions.Count == 0)
        {
            var declaration = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var fileNamespace = declaration?.Name.ToString() ?? assembly;
            var owner = map.Resolve(fileNamespace, declared: true)?.Owner;
            if (owner is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Unowned,
                    declaration?.Name.GetLocation() ?? Location.Create(root.SyntaxTree, default), fileNamespace));
            }

            attributions.Add(new Attribution(null, $"<file>:{Relative(path)}", owner, fileNamespace));
        }

        return attributions;
    }

    // GetSymbolInfo names what the source spells; the compiler also calls conversion operators, collection-initializer
    // Adds and the foreach enumerator pattern on its own.
    private static IEnumerable<ISymbol?> BoundSymbols(SemanticModel model, SyntaxNode node) => node switch
    {
        ExpressionSyntax expression =>
        [
            model.GetSymbolInfo(expression).Symbol,
            model.GetConversion(expression).MethodSymbol,
            expression.Parent.IsKind(SyntaxKind.CollectionInitializerExpression)
                ? model.GetCollectionInitializerSymbolInfo(expression).Symbol
                : null,
        ],
        CommonForEachStatementSyntax loop when model.GetForEachStatementInfo(loop) is var info =>
            [info.GetEnumeratorMethod, info.MoveNextMethod, info.CurrentProperty, info.DisposeMethod, info.ElementConversion.MethodSymbol],
        _ => [],
    };

    private static IEnumerable<INamedTypeSymbol> ReferencedTypes(ISymbol? symbol) => symbol switch
    {
        ITypeSymbol type => TypesIn(type),
        IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol => TypesIn(symbol.ContainingType),
        _ => [],
    };

    private static IEnumerable<INamedTypeSymbol> TypesIn(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol named => named.TypeArguments.SelectMany(TypesIn).Prepend(named),
        IArrayTypeSymbol array => TypesIn(array.ElementType),
        IPointerTypeSymbol pointer => TypesIn(pointer.PointedAtType),
        _ => [],
    };

    private sealed class Attribution(SyntaxNode? scope, string symbol, string? owner, string ns)
    {
        public SyntaxNode? Scope { get; } = scope;

        public string Symbol { get; } = symbol;

        public string? Owner { get; } = owner;

        public string Namespace { get; } = ns;
    }

    private static bool IsTypeDeclaration(SyntaxNode node) => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;

    private static Location IdentifierLocation(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.GetLocation(),
        DelegateDeclarationSyntax d => d.Identifier.GetLocation(),
        _ => node.GetLocation(),
    };

    private static string Identifier(SyntaxNode node) => node switch
    {
        TypeDeclarationSyntax type => GenericIdentifier(type.Identifier.ValueText, type.TypeParameterList),
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax d => GenericIdentifier(d.Identifier.ValueText, d.TypeParameterList),
        _ => throw new InvalidOperationException($"{node.Kind()} is not a type declaration."),
    };

    // Arity only: type parameter names are not part of a generic type's identity.
    private static string GenericIdentifier(string identifier, TypeParameterListSyntax? parameters) =>
        parameters is null ? identifier : $"{identifier}<{new string(',', parameters.Parameters.Count - 1)}>";

    private static bool IsFileLocal(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Modifiers.Any(SyntaxKind.FileKeyword),
        DelegateDeclarationSyntax d => d.Modifiers.Any(SyntaxKind.FileKeyword),
        _ => false,
    };

    private static string NamespaceOf(SyntaxNode node, string assembly)
    {
        var parts = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse().ToList();
        return parts.Count == 0 ? assembly : string.Join(".", parts);
    }

    // ponytail: repo-relative by the last "/src/" segment, as ModuleLedgerScanner prints it; a checkout path without
    // one keeps the absolute path, which only file-scoped symbols would notice.
    private static string Relative(string path)
    {
        var cut = path.LastIndexOf("/src/", StringComparison.Ordinal);
        return cut < 0 ? path : path.Substring(cut + 1);
    }
}
