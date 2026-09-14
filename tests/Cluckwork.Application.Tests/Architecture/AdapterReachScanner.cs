namespace Cluckwork.Application.Tests.Architecture;

using System.Text.Json;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    int ExpectedAdapterCountFloor);

public static class AdapterReachScanner
{
    internal const int RealTreeAdapterFloor = 40;

    public static AdapterReachReport Scan(string srcRoot, string ledgerPath)
    {
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)!;
        var ledger = ModuleLedger.Load(ledgerPath);
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
        var declaredTypes = roots.SelectMany(root => root.DescendantNodes())
            .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Select(node => TypeName(node)).ToHashSet(StringComparer.Ordinal);
        var live = new List<AdapterReach>();
        var persistence = new List<string>();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var root in roots)
        {
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var ns = NamespaceOf(type);
                var typeName = TypeName(type);
                if (!ledger.AdapterRoots.Namespaces.Any(prefix => Under(ns, prefix))
                    && !ledger.AdapterRoots.Types.Contains(typeName, StringComparer.Ordinal))
                {
                    continue;
                }

                var banPersistence = ledger.AdapterRoots.PersistenceForbiddenNamespaces.Any(prefix => Under(ns, prefix));
                var adapters = type.Members.OfType<BaseMethodDeclarationSyntax>()
                    .Where(m => m is MethodDeclarationSyntax or ConstructorDeclarationSyntax)
                    .Select(m => (Node: (SyntaxNode)m, Parameters: m.ParameterList,
                        Name: m is MethodDeclarationSyntax method ? method.Identifier.ValueText : "ctor"))
                    .ToList();
                if (type.ParameterList is { } primaryParameters)
                {
                    adapters.Add((type, primaryParameters, "ctor"));
                }

                foreach (var (node, parameters, name) in adapters)
                {
                    count++;
                    var symbol = $"{TypeName(type, includeArity: true)}.{name}";
                    var file = Relative(repoRoot, root.SyntaxTree.FilePath);
                    var imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                        .Where(u => u.Parent is CompilationUnitSyntax || node.AncestorsAndSelf().Contains(u.Parent))
                        .ToList();
                    var types = parameters.Parameters.Select(p => p.Type).OfType<TypeSyntax>().ToList();
                    if (node is BaseMethodDeclarationSyntax method)
                    {
                        // Inline handlers belong to the mapping adapter, not to separate ledger rows.
                        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                        if (body is not null)
                        {
                            types.AddRange(body.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>()
                                .SelectMany(lambda => lambda.ParameterList.Parameters)
                                .Select(parameter => parameter.Type).OfType<TypeSyntax>());
                            types.AddRange(body.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>()
                                .Select(lambda => lambda.Parameter.Type).OfType<TypeSyntax>());
                            types.AddRange(body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                                .SelectMany(ServiceTypes));
                        }
                    }

                    foreach (var syntax in types)
                    {
                        foreach (var named in NamedTypes(syntax))
                        {
                            Record(named, named, new HashSet<string>(StringComparer.Ordinal));
                        }
                    }

                    void Record(NameSyntax named, SyntaxNode location, HashSet<string> expandedAliases)
                    {
                        var dotted = ModuleLedgerScanner.DottedText(named)!;
                        var firstDot = dotted.IndexOf('.');
                        var aliasName = firstDot < 0 ? dotted : dotted[..firstDot];
                        var alias = imports.FirstOrDefault(u => u.Alias?.Name.Identifier.ValueText == aliasName);
                        if (alias?.Name is { } aliasedType && expandedAliases.Add(aliasName))
                        {
                            if (firstDot >= 0)
                            {
                                Record(SyntaxFactory.ParseName(ModuleLedgerScanner.DottedText(aliasedType) + dotted[firstDot..]),
                                    location, expandedAliases);
                            }
                            else
                            {
                                foreach (var aliasedName in NamedTypes(aliasedType))
                                {
                                    Record(aliasedName, location, expandedAliases);
                                }
                            }
                            return;
                        }

                        var line = location.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
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
            }
        }

        var ordered = live.Distinct().OrderBy(r => r.Symbol, StringComparer.Ordinal)
            .ThenBy(r => r.Owner, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
        var declared = ledger.Adapters.GroupBy(a => a.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(a => a.Reaches).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var undeclared = ordered.Where(r => !declared.TryGetValue(r.Symbol, out var owners) || !owners.Contains(r.Owner)).ToList();
        var reached = ordered.Select(r => (r.Symbol, r.Owner)).ToHashSet();
        var reachedSymbols = ordered.Select(r => r.Symbol).ToHashSet(StringComparer.Ordinal);
        var loosenable = ledger.Adapters.Select(a => new AdapterClaim(a.Symbol,
                a.Reaches.Where(owner => !reached.Contains((a.Symbol, owner))).Order(StringComparer.Ordinal).ToArray()))
            .Where(a => a.Reaches.Count > 0 || !reachedSymbols.Contains(a.Symbol))
            .OrderBy(a => a.Symbol, StringComparer.Ordinal).ToList();
        var floor = GuardScanner.FindRepoRoot(AppContext.BaseDirectory) is { } realRoot
            && srcFull == Path.Combine(realRoot, "src") ? RealTreeAdapterFloor : count;
        return new AdapterReachReport(ordered, undeclared, loosenable, persistence, unresolved.ToList(),
            parseErrors, errors, count, floor);
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
        failures.AddRange(report.PersistenceViolations);
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
        if (ledger.AdapterRoots.Namespaces.Count + ledger.AdapterRoots.Types.Count == 0)
        {
            errors.Add("adapterRoots declares no namespaces or types");
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

    private static IEnumerable<TypeSyntax> ServiceTypes(InvocationExpressionSyntax call)
    {
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            SimpleNameSyntax simple => simple,
            _ => null,
        };
        if (name?.Identifier.ValueText is "GetRequiredService" or "GetService"
            or "GetKeyedService" or "GetRequiredKeyedService")
        {
            return name is GenericNameSyntax generic
                ? generic.TypeArgumentList.Arguments
                : call.ArgumentList.Arguments.Select(argument => argument.Expression)
                    .OfType<TypeOfExpressionSyntax>().Select(typeOf => typeOf.Type);
        }
        if (name is GenericNameSyntax { Identifier.ValueText: "CreateInstance" } create
            && call.Expression is MemberAccessExpressionSyntax access
            && ModuleLedgerScanner.DottedText(access.Expression) is { } receiver
            && (receiver == "ActivatorUtilities" || receiver == "Microsoft.Extensions.DependencyInjection.ActivatorUtilities"))
        {
            return create.TypeArgumentList.Arguments;
        }
        return [];
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
