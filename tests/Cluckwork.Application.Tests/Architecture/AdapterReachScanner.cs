using System.Text.Json;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

public sealed record AdapterReach(string Symbol, string Owner, string Type, string File, int Line);

public sealed record AdapterReachReport(
    IReadOnlyList<AdapterReach> LiveReach,
    IReadOnlyList<AdapterReach> Undeclared,
    IReadOnlyList<AdapterClaim> Loosenable,
    IReadOnlyList<string> PersistenceViolations,
    IReadOnlyList<string> UnresolvedTypes,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<string> RegistryErrors,
    int WalkedAdapterCount,
    int ExpectedAdapterCountFloor)
{
    public int TopLevelProgramAdapterCount { get; init; }
    public IReadOnlyList<string> RouteErrors { get; init; } = [];
    public IReadOnlyList<AdapterReach> ContractBypasses { get; init; } = [];
    public IReadOnlyList<string> AliasErrors { get; init; } = [];
}

public static class AdapterReachScanner
{
    internal const int RealTreeAdapterFloor = 40;
    internal const int RealTreePeerFloor = 800;

    private static readonly Dictionary<string, string?> ResolverCalls = new(StringComparer.Ordinal)
    {
        ["GetService"] = null,
        ["GetRequiredService"] = null,
        ["GetServices"] = null,
        ["GetKeyedService"] = null,
        ["GetRequiredKeyedService"] = null,
        ["GetKeyedServices"] = null,
        ["CreateInstance"] = "ActivatorUtilities",
        ["GetServiceOrCreateInstance"] = "ActivatorUtilities",
    };

    private static readonly HashSet<string> RouteCalls = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "MapFallback", "Map",
    };

    private sealed record ProgramRoute(string Name, IReadOnlyList<SyntaxNode> Handlers);

    public static AdapterReachReport Scan(string srcRoot, ModuleLedger ledger) =>
        Scan(srcRoot, ledger, peers: false);

    // #1023: the same walk with every module's own namespaces and claimed types as the roots. A member
    // reaching another contracted module outside its contract and seam is a bypass; its own module and
    // Platform are free. Adapter rows do not apply, so nothing is undeclared or loosenable.
    public static AdapterReachReport ScanPeers(string srcRoot, ModuleLedger ledger)
    {
        var modules = ledger.Owners.Where(o => o.Kind == ModuleLedger.ModuleKind).ToList();
        return Scan(srcRoot, ledger with
        {
            AdapterRoots = new AdapterRoots(
                modules.SelectMany(o => o.Namespaces.Concat(o.ExactNamespaces)).ToList(),
                modules.SelectMany(o => o.Types).ToList())
            {
                PersistenceForbiddenNamespaces = ledger.AdapterRoots.PersistenceForbiddenNamespaces,
            },
            AdapterTiers = [],
        }, peers: true);
    }

    private static AdapterReachReport Scan(string srcRoot, ModuleLedger ledger, bool peers)
    {
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)!;
        var errors = new List<string>(ledger.RegistryErrors);
        var claims = ModuleLedgerScanner.BuildNamespaceIndex(ledger, errors);
        var kinds = ledger.Owners.GroupBy(o => o.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        ValidateRegistry(ledger, kinds, errors);

        var roots = GuardScanner.EnumerateSourceFiles(srcFull).Select(file =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(file), ModuleLedgerScanner.ParseOptions, file)
                .GetCompilationUnitRoot()).ToList();
        var parseErrors = roots.SelectMany(root => root.SyntaxTree.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{Relative(repoRoot, d.Location.SourceTree!.FilePath)}:" +
                $"{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}").ToList();
        var declarations = roots.SelectMany(root => root.DescendantNodes())
            .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax).ToList();
        var declaredTypes = declarations.Select(node => TypeName(node)).ToHashSet(StringComparer.Ordinal);
        // #1023: a ledger entry and a resolved reference both drop generic arity, so an entry admits every
        // generic homonym, and a claim would own nested types the peer walk never roots. Entries therefore
        // name only top-level, non-generic types, and a claimed type declares no nested types.
        var nested = declarations.Where(n => n.Ancestors().Any(a => a is BaseTypeDeclarationSyntax))
            .Select(node => TypeName(node)).ToHashSet(StringComparer.Ordinal);
        var unsupported = declarations.Where(n => n.AncestorsAndSelf().Any(a =>
                a is TypeDeclarationSyntax { TypeParameterList: not null } or DelegateDeclarationSyntax { TypeParameterList: not null }))
            .Select(node => TypeName(node)).Concat(nested).ToHashSet(StringComparer.Ordinal);
        var allowed = ledger.Owners.Where(o => o.Contract.Count > 0).ToDictionary(o => o.Name,
            o => o.Contract.Concat(peers ? o.Seam : []).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var owner in ledger.Owners)
        {
            foreach (var (list, types) in new[] { ("contract", owner.Contract), ("seam", owner.Seam) })
            {
                foreach (var type in types.Order(StringComparer.Ordinal))
                {
                    if (!declaredTypes.Contains(type))
                    {
                        errors.Add($"owner '{owner.Name}' {list} type '{type}' is not declared under src/");
                    }
                    else if (ModuleLedgerScanner.Resolve(claims, type, declared: false)?.Owner != owner.Name)
                    {
                        errors.Add($"owner '{owner.Name}' {list} type '{type}' is not in a namespace '{owner.Name}' owns");
                    }
                    else if (unsupported.Contains(type))
                    {
                        errors.Add($"owner '{owner.Name}' {list} type '{type}' names a generic or nested declaration; " +
                            "an entry carries no arity, so it may name only a top-level, non-generic type");
                    }
                }
            }
            foreach (var type in owner.Types.Order(StringComparer.Ordinal))
            {
                var home = type.Contains('.', StringComparison.Ordinal)
                    ? ModuleLedgerScanner.Resolve(claims, type[..type.LastIndexOf('.')], declared: false) : null;
                if (!declaredTypes.Contains(type))
                {
                    errors.Add($"owner '{owner.Name}' claims type '{type}', which is not declared under src/");
                }
                else if (home is null || kinds[home.Value.Owner] != ModuleLedger.PlatformKind)
                {
                    errors.Add($"owner '{owner.Name}' claims type '{type}' outside a platform namespace; " +
                        "only a type the free hub holds can be claimed");
                }
                else if (unsupported.Contains(type))
                {
                    errors.Add($"owner '{owner.Name}' claims type '{type}', which names a generic or nested declaration; " +
                        "a claim carries no arity, so it may name only a top-level, non-generic type");
                }
                else if (nested.Any(n => n.StartsWith(type + ".", StringComparison.Ordinal)))
                {
                    errors.Add($"owner '{owner.Name}' claims type '{type}', which declares nested types; " +
                        "the claim would own them but the peer walk roots only the claimed type");
                }
            }
        }
        var live = new List<AdapterReach>();
        var persistence = new List<string>();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var count = 0;
        var programCount = 0;
        var routeErrors = new List<string>();
        var aliasErrors = new List<string>();

        foreach (var root in roots)
        {
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var ns = NamespaceOf(type);
                var typeName = TypeName(type);
                if (!ledger.AdapterNamespaces.Any(prefix => Under(ns, prefix))
                    && !ledger.AdapterRoots.Types.Contains(typeName, StringComparer.Ordinal))
                {
                    continue;
                }

                var banPersistence = ledger.PersistenceForbiddenNamespaces.Any(prefix => Under(ns, prefix));
                var adapters = type.Members.OfType<BaseMethodDeclarationSyntax>()
                    .Where(m => m is MethodDeclarationSyntax or ConstructorDeclarationSyntax)
                    .Select(m => (Node: (SyntaxNode)m,
                        Name: m is MethodDeclarationSyntax method ? method.Identifier.ValueText : "ctor"))
                    .ToList();
                if (type.ParameterList is not null)
                {
                    adapters.Add((type, "ctor"));
                }

                foreach (var (node, name) in adapters)
                {
                    count++;
                    ScanAdapter(node, ns, typeName, $"{TypeName(type, includeArity: true)}.{name}", banPersistence);
                }
            }

            var projectNamespace = ModuleLedgerScanner.ProjectRootNamespace(srcFull, root.SyntaxTree.FilePath);
            if (ledger.AdapterRoots.TopLevelPrograms.Contains(projectNamespace, StringComparer.Ordinal))
            {
                foreach (var route in ProgramRoutes(root, projectNamespace, roots, declaredTypes, claims, routeErrors))
                {
                    count++;
                    programCount++;
                    foreach (var handler in route.Handlers)
                    {
                        var enclosingType = handler.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                        ScanAdapter(handler, enclosingType is null ? projectNamespace : NamespaceOf(handler),
                            enclosingType is null ? projectNamespace + ".Program" : TypeName(enclosingType),
                            $"{projectNamespace}.Program.{route.Name}", banPersistence: true);
                    }
                }
            }
        }

        void ScanAdapter(SyntaxNode node, string ns, string typeName, string symbol, bool banPersistence)
        {
            var file = Relative(repoRoot, node.SyntaxTree.FilePath);
            var imports = ImportsOf(node);
            foreach (var syntax in AdapterTypes(node))
            {
                foreach (var named in NamedTypes(syntax))
                {
                    Record(named, named, new HashSet<string>(StringComparer.Ordinal));
                }
            }

            void Record(NameSyntax named, SyntaxNode location, HashSet<string> expandedAliases)
            {
                var line = location.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                // A whole-name alias may target a generic or tuple type, so the named types inside it are walked in turn.
                if (named is SimpleNameSyntax simple
                    && AliasTarget(simple.Identifier.ValueText, imports) is { } aliasedType
                    && expandedAliases.Add(simple.Identifier.ValueText))
                {
                    foreach (var aliasedName in NamedTypes(aliasedType))
                    {
                        Record(aliasedName, location, expandedAliases);
                    }
                    return;
                }

                if (ExpandAlias(named, imports) is not { } dotted)
                {
                    aliasErrors.Add($"cannot resolve the alias qualifier in {named} in {symbol} at {file}:{line}; " +
                        "the walk cannot be trusted with a name it cannot qualify");
                    return;
                }
                if (IsPersistence(dotted))
                {
                    if (banPersistence)
                    {
                        persistence.Add($"forbidden persistence type {dotted} in {symbol} at {file}:{line}");
                    }
                    return;
                }

                var resolved = ResolveType(dotted, ns, typeName, imports, declaredTypes, claims);
                if (resolved is null)
                {
                    // No semantic model: an unmatched simple name is not assigned to an arbitrary import.
                    unresolved.Add($"{symbol}: {dotted} at {file}:{line}");
                    return;
                }

                var owner = ModuleLedgerScanner.Resolve(claims, resolved, declared: false);
                if (owner is { } found && kinds[found.Owner] == ModuleLedger.ModuleKind)
                {
                    live.Add(new AdapterReach(symbol, found.Owner, resolved, file, line));
                }
            }
        }

        var ordered = live.Distinct().OrderBy(r => r.Symbol, StringComparer.Ordinal)
            .ThenBy(r => r.Owner, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
        var declared = ledger.Adapters.GroupBy(a => a.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(a => a.Reaches).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var undeclared = peers ? []
            : ordered.Where(r => !declared.TryGetValue(r.Symbol, out var owners) || !owners.Contains(r.Owner)).ToList();
        var reached = ordered.Select(r => (r.Symbol, r.Owner)).ToHashSet();
        var reachedSymbols = ordered.Select(r => r.Symbol).ToHashSet(StringComparer.Ordinal);
        var loosenable = peers ? [] : ledger.Adapters.Select(a => new AdapterClaim(a.Symbol,
                a.Reaches.Where(owner => !reached.Contains((a.Symbol, owner))).Order(StringComparer.Ordinal).ToArray()))
            .Where(a => a.Reaches.Count > 0 || !reachedSymbols.Contains(a.Symbol))
            .OrderBy(a => a.Symbol, StringComparer.Ordinal).ToList();
        var floor = GuardScanner.FindRepoRoot(AppContext.BaseDirectory) is { } realRoot
            && srcFull == Path.Combine(realRoot, "src") ? (peers ? RealTreePeerFloor : RealTreeAdapterFloor) : count;
        return new AdapterReachReport(ordered, undeclared, loosenable, persistence, unresolved.ToList(),
            parseErrors, errors, count, floor)
        {
            TopLevelProgramAdapterCount = programCount,
            RouteErrors = routeErrors,
            AliasErrors = aliasErrors,
            ContractBypasses = ordered.Where(r => allowed.TryGetValue(r.Owner, out var types) && !types.Contains(r.Type)
                && (!peers || (ModuleLedgerScanner.Resolve(claims, r.Symbol, declared: false)?.Owner is { } from
                    && kinds[from] == ModuleLedger.ModuleKind && from != r.Owner))).ToList(),
        };
    }

    public static IReadOnlyList<string> Evaluate(AdapterReachReport report)
    {
        var failures = report.RegistryErrors.Select(e => $"adapter registry error: {e}").ToList();
        if (report.ParseErrors.Count > 0)
        {
            failures.Add("the walk cannot be trusted:\n" + string.Join("\n", report.ParseErrors));
        }
        if (report.WalkedAdapterCount < report.ExpectedAdapterCountFloor)
        {
            failures.Add($"walked {report.WalkedAdapterCount} adapters, expected at least {report.ExpectedAdapterCountFloor}");
        }
        failures.AddRange(report.RouteErrors);
        failures.AddRange(report.AliasErrors);
        failures.AddRange(report.PersistenceViolations);
        foreach (var bypass in report.ContractBypasses)
        {
            failures.Add($"contract bypass {bypass.Symbol} -> {bypass.Owner} through {bypass.Type} at " +
                $"{bypass.File}:{bypass.Line}; {bypass.Owner} declares a contract, so call one of its contract types");
        }
        foreach (var reach in report.Undeclared)
        {
            failures.Add($"undeclared adapter reach {reach.Symbol} -> {reach.Owner} through {reach.Type} " +
                $"at {reach.File}:{reach.Line}; review and add the JSON row:\n" +
                RenderAdapters(report.LiveReach.Where(r => r.Symbol == reach.Symbol)));
        }
        return failures;
    }

    internal static string RenderAdapters(IEnumerable<AdapterReach> reaches) =>
        JsonSerializer.Serialize(reaches.GroupBy(r => r.Symbol, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new
            {
                symbol = g.Key,
                reaches = g.Select(r => r.Owner).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            }), new JsonSerializerOptions { WriteIndented = true });

    private static void ValidateRegistry(ModuleLedger ledger, IReadOnlyDictionary<string, string> kinds, List<string> errors)
    {
        if (ledger.AdapterRoots.Namespaces.Count + ledger.AdapterRoots.Types.Count + ledger.AdapterRoots.TopLevelPrograms.Count == 0)
        {
            errors.Add("adapterRoots declares no namespaces, types or topLevelPrograms");
        }
        if (ledger.AdapterRoots.PersistenceForbiddenNamespaces.Count == 0)
        {
            errors.Add("adapterRoots declares no persistenceForbiddenNamespaces");
        }
        foreach (var duplicate in ledger.Adapters.GroupBy(a => a.Symbol, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"duplicate adapter symbol '{duplicate.Key}'");
        }
        foreach (var row in ledger.Adapters)
        {
            if (string.IsNullOrWhiteSpace(row.Symbol))
            {
                errors.Add("blank adapter symbol");
            }
            foreach (var owner in row.Reaches)
            {
                if (!kinds.TryGetValue(owner, out var kind))
                {
                    errors.Add($"adapter '{row.Symbol}' references unknown owner '{owner}'");
                }
                else if (kind == ModuleLedger.PlatformKind)
                {
                    errors.Add($"adapter '{row.Symbol}' names platform owner '{owner}'");
                }
            }
        }
    }

    private static string? ResolveType(string dotted, string ns, string enclosingType,
        IReadOnlyList<UsingDirectiveSyntax> imports, HashSet<string> declaredTypes,
        IReadOnlyDictionary<string, ModuleLedgerScanner.Claim> claims)
    {
        if (dotted.StartsWith("Cluckwork.", StringComparison.Ordinal))
        {
            return dotted;
        }
        foreach (var import in imports.Where(u => u.Alias is null && u.Name is not null))
        {
            var imported = ModuleLedgerScanner.DottedText(import.Name)!;
            var candidate = $"{imported}.{dotted}";
            foreach (var qualified in NamespaceCandidates(candidate, ns))
            {
                if (declaredTypes.Contains(qualified))
                {
                    return qualified;
                }
            }
        }
        var prefix = enclosingType;
        while (prefix.Contains('.', StringComparison.Ordinal))
        {
            var candidate = $"{prefix}.{dotted}";
            if (declaredTypes.Contains(candidate))
            {
                return candidate;
            }
            prefix = prefix[..prefix.LastIndexOf('.')];
        }
        if (dotted.Contains('.', StringComparison.Ordinal)
            && ModuleLedgerScanner.ResolveReferenced(claims, dotted, ns) is { } relative)
        {
            return NamespaceCandidates(dotted, ns).First(candidate => Under(candidate, relative.Namespace));
        }
        return null;
    }

    private static IEnumerable<string> NamespaceCandidates(string dotted, string ns)
    {
        yield return dotted;
        var prefix = ns;
        while (true)
        {
            yield return $"{prefix}.{dotted}";
            var cut = prefix.LastIndexOf('.');
            if (cut < 0)
            {
                break;
            }
            prefix = prefix[..cut];
        }
    }

    private static IEnumerable<TypeSyntax> AdapterTypes(SyntaxNode node)
    {
        if (node is TypeDeclarationSyntax primary)
        {
            return primary.ParameterList!.Parameters.Select(p => p.Type).OfType<TypeSyntax>();
        }

        // Nested handler parameters belong to the enclosing adapter, including anonymous methods and local functions.
        return node.DescendantNodesAndSelf().OfType<ParameterSyntax>().Select(p => p.Type).OfType<TypeSyntax>()
            .Concat(node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().SelectMany(ServiceTypes));
    }

    private static SimpleNameSyntax? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name,
        MemberBindingExpressionSyntax binding => binding.Name,
        SimpleNameSyntax simple => simple,
        _ => null,
    };

    private static IEnumerable<TypeSyntax> ServiceTypes(InvocationExpressionSyntax call)
    {
        var name = CalledName(call);
        if (name is null || !ResolverCalls.TryGetValue(name.Identifier.ValueText, out var requiredReceiver))
        {
            return [];
        }
        if (requiredReceiver is not null)
        {
            var qualifiedReceiver = "Microsoft.Extensions.DependencyInjection." + requiredReceiver;
            var receiver = call.Expression is MemberAccessExpressionSyntax access
                ? ExpandAlias(access.Expression, ImportsOf(call)) : null;
            var staticImport = call.Expression is SimpleNameSyntax && ImportsOf(call)
                .Any(import => import.StaticKeyword != default
                    && ModuleLedgerScanner.DottedText(import.Name) == qualifiedReceiver);
            if (receiver != requiredReceiver && receiver != qualifiedReceiver && !staticImport)
            {
                return [];
            }
        }
        return name is GenericNameSyntax generic
            ? generic.TypeArgumentList.Arguments
            : call.ArgumentList.Arguments.Select(argument => argument.Expression)
                .OfType<TypeOfExpressionSyntax>().Select(typeOf => typeOf.Type);
    }

    // NamespaceOrType, not Name: a tuple alias has no Name.
    private static TypeSyntax? AliasTarget(string? alias, IReadOnlyList<UsingDirectiveSyntax> imports) =>
        imports.FirstOrDefault(u => u.Alias?.Name.Identifier.ValueText == alias)?.NamespaceOrType;

    // The one place a written name becomes a qualified one: a leftmost import alias, written Alias.X or
    // Alias::X, is replaced by its target, and global:: is already fully qualified. Null means a qualifier
    // the walk cannot resolve.
    private static string? ExpandAlias(SyntaxNode name, IReadOnlyList<UsingDirectiveSyntax> imports)
    {
        if (ModuleLedgerScanner.DottedText(name) is not { } dotted)
        {
            return null;
        }
        var leftmost = name;
        while (leftmost is QualifiedNameSyntax or MemberAccessExpressionSyntax)
        {
            leftmost = leftmost is QualifiedNameSyntax qualified ? qualified.Left : ((MemberAccessExpressionSyntax)leftmost).Expression;
        }
        if (leftmost is AliasQualifiedNameSyntax aliasQualified)
        {
            var qualifier = aliasQualified.Alias.Identifier.ValueText;
            if (qualifier == "global")
            {
                return dotted;
            }
            return AliasTarget(qualifier, imports) is NameSyntax target ? $"{ModuleLedgerScanner.DottedText(target)}.{dotted}" : null;
        }
        return leftmost is IdentifierNameSyntax identifier
            && AliasTarget(identifier.Identifier.ValueText, imports) is NameSyntax prefix
            ? ModuleLedgerScanner.DottedText(prefix) + dotted[identifier.Identifier.ValueText.Length..]
            : dotted;
    }

    private static IReadOnlyList<UsingDirectiveSyntax> ImportsOf(SyntaxNode node) =>
        node.SyntaxTree.GetCompilationUnitRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.Parent is CompilationUnitSyntax || node.AncestorsAndSelf().Contains(u.Parent)).ToList();

    private static IEnumerable<ProgramRoute> ProgramRoutes(CompilationUnitSyntax root, string projectNamespace,
        IReadOnlyList<CompilationUnitSyntax> roots, HashSet<string> declaredTypes,
        IReadOnlyDictionary<string, ModuleLedgerScanner.Claim> claims, List<string> errors)
    {
        foreach (var call in root.Members.OfType<GlobalStatementSyntax>()
            .SelectMany(statement => statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(call => CalledName(call) is { } name && RouteCalls.Contains(name.Identifier.ValueText)))
        {
            var mapping = CalledName(call)!.Identifier.ValueText;
            var methodList = mapping == "MapMethods" ? LiteralMethodList(call) : null;
            var route = call.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
                .FirstOrDefault(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText;
            foreach (var argument in call.ArgumentList.Arguments)
            {
                var expression = argument.Expression;
                while (expression is ParenthesizedExpressionSyntax or CastExpressionSyntax)
                {
                    expression = expression is ParenthesizedExpressionSyntax parenthesized
                        ? parenthesized.Expression : ((CastExpressionSyntax)expression).Expression;
                }
                IReadOnlyList<SyntaxNode> handlers;
                string? methodName = null;
                if (expression is AnonymousFunctionExpressionSyntax)
                {
                    handlers = [expression];
                }
                else if (argument == call.ArgumentList.Arguments.Last()
                    && expression is SimpleNameSyntax or MemberAccessExpressionSyntax)
                {
                    methodName = expression is SimpleNameSyntax identifier ? identifier.Identifier.ValueText
                        : ((MemberAccessExpressionSyntax)expression).Name.Identifier.ValueText;
                    handlers = ResolveRouteHandlers(expression, methodName, call, projectNamespace, roots, declaredTypes, claims);
                }
                else
                {
                    continue;
                }

                if (handlers.Count == 0 || (route is null && methodName is null))
                {
                    errors.Add($"unresolved top-level route handler {expression} in {root.SyntaxTree.FilePath}:" +
                        $"{expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1}; " +
                        "the walk cannot be trusted without a source handler declaration and a route literal or method name");
                }
                var name = $"{mapping}({route}{(methodList is null ? string.Empty : ";" + methodList)})";
                yield return new ProgramRoute(name + (methodName is null ? string.Empty : "." + methodName), handlers);
            }
        }
    }

    private static string? LiteralMethodList(InvocationExpressionSyntax call)
    {
        foreach (var argument in call.ArgumentList.Arguments)
        {
            IEnumerable<ExpressionSyntax>? elements = argument.Expression switch
            {
                ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions,
                ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
                ObjectCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
                CollectionExpressionSyntax collection when collection.Elements.All(e => e is ExpressionElementSyntax)
                    => collection.Elements.Cast<ExpressionElementSyntax>().Select(e => e.Expression),
                _ => null,
            };
            if (elements is null)
            {
                continue;
            }
            var items = elements.ToList();
            if (items.All(item => item is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                // HTTP method order and source formatting do not change the route's reach allowance.
                return "[" + string.Join(",", items.Cast<LiteralExpressionSyntax>().Select(item => item.Token.ValueText)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "]";
            }
        }
        return null;
    }

    private static IReadOnlyList<SyntaxNode> ResolveRouteHandlers(ExpressionSyntax expression, string methodName,
        InvocationExpressionSyntax call, string projectNamespace, IReadOnlyList<CompilationUnitSyntax> roots,
        HashSet<string> declaredTypes, IReadOnlyDictionary<string, ModuleLedgerScanner.Claim> claims)
    {
        var root = call.SyntaxTree.GetCompilationUnitRoot();
        if (expression is SimpleNameSyntax)
        {
            var locals = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
                .Where(local => local.Identifier.ValueText == methodName
                    && (local.Parent is GlobalStatementSyntax || call.Ancestors().Contains(local.Parent)))
                .Cast<SyntaxNode>().ToList();
            if (locals.Count > 0)
            {
                return locals;
            }
        }

        var imports = ImportsOf(call);
        var typeNames = expression is MemberAccessExpressionSyntax member
            ? new[] { ModuleLedgerScanner.DottedText(member.Expression) }.OfType<string>()
            : imports.Where(u => u.StaticKeyword != default).Select(u => ModuleLedgerScanner.DottedText(u.Name))
                .OfType<string>().Append(projectNamespace + ".Program");
        var resolvedTypes = typeNames.Select(name => ResolveType(name, projectNamespace, projectNamespace + ".Program",
            imports, declaredTypes, claims)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return roots.SelectMany(unit => unit.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => method.Identifier.ValueText == methodName
                && method.Parent is TypeDeclarationSyntax type
                && (resolvedTypes.Contains(TypeName(type))
                    || (expression is SimpleNameSyntax && type.SyntaxTree == root.SyntaxTree
                        && type.Identifier.ValueText == "Program" && NamespaceOf(type) == "<global>")))
            .Cast<SyntaxNode>().ToList();
    }

    private static IEnumerable<NameSyntax> NamedTypes(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<NameSyntax>().Where(n => n.Parent is not NameSyntax);

    private static bool IsPersistence(string dotted) =>
        dotted[(dotted.LastIndexOf('.') + 1)..] is "AppDbContext" or "DbContext" or "DbSet" or "IQueryable";

    private static bool Under(string name, string prefix) =>
        name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal);

    private static string NamespaceOf(SyntaxNode node) => ModuleLedgerScanner.NamespaceOf(node, "<global>");

    private static string TypeName(SyntaxNode node, bool includeArity = false) => NamespaceOf(node) + "." + string.Join(".",
        node.AncestorsAndSelf().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Reverse().Select(n =>
            {
                var name = n is BaseTypeDeclarationSyntax type ? type.Identifier.ValueText
                    : ((DelegateDeclarationSyntax)n).Identifier.ValueText;
                return includeArity && n is TypeDeclarationSyntax { TypeParameterList: { } parameters }
                    ? $"{name}<{new string(',', parameters.Parameters.Count - 1)}>" : name;
            }));

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
}
