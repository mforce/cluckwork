using System.Reflection;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

// #842 (epic #514 slice 1) — the cross-owner edge ratchet over src/, read against RealModuleLedger.


public sealed record CrossOwnerEdge(
    string From,
    string To,
    string Symbol,
    IReadOnlyList<string> ReferencedNamespaces,
    string File,
    int Line);

public sealed record StaleLedgerSymbol(string From, string To, string Symbol, string Reason);

public sealed record GlobalModuleImport(string Namespace, string File, int Line);

public sealed record ModuleLedgerReport(
    IReadOnlyList<CrossOwnerEdge> LiveEdges,
    IReadOnlyList<CrossOwnerEdge> UndeclaredEdges,
    IReadOnlyList<StaleLedgerSymbol> StaleSymbols,
    IReadOnlyList<string> UnownedNamespaces,
    IReadOnlyList<GlobalModuleImport> GlobalModuleImports,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<string> RegistryErrors,
    int ScannedFileCount,
    int ExpectedFileCountFloor);

public static class ModuleLedgerScanner
{
    // Below the 466 files src/ held on 2026-09-14, so growth never reds the gate and a dropped subtree does.
    internal const int RealTreeFileFloor = 400;

    // The symbols this test build compiled with, embedded by the csproj (#1053). src/ shares its
    // target framework and configuration; UndeclaredDefineConstants catches a project adding its own.
    internal static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithPreprocessorSymbols(
        typeof(ModuleLedgerScanner).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "DefineConstants")?.Value?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? throw new InvalidOperationException("The test assembly carries no DefineConstants metadata."));

    private const string Prefix = "Cluckwork.";

    private static readonly string[] MsBuildFileSkipDirectories =
        ["bin", "obj", "node_modules", ".git", "web"];

    // A constant guards an `#if` branch the fixed ParseOptions never parse (#843). No project
    // graph exists here, so every MSBuild file is read textually, Condition or not.
    internal static IReadOnlyList<(string ProjectFile, string Symbol)> UndeclaredDefineConstants(string repoRoot)
    {
        var declared = new HashSet<string>(ParseOptions.PreprocessorSymbolNames, StringComparer.Ordinal);
        var undeclared = new List<(string, string)>();

        foreach (var file in EnumerateMsBuildFiles(repoRoot))
        {
            var document = System.Xml.Linq.XDocument.Load(file);
            foreach (var element in document.Descendants("DefineConstants"))
            {
                foreach (var symbol in element.Value.Split(
                    ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (symbol.StartsWith("$(", StringComparison.Ordinal) || declared.Contains(symbol))
                    {
                        continue;
                    }

                    undeclared.Add((Relative(repoRoot, file), symbol));
                }
            }
        }

        return undeclared;
    }

    private static IReadOnlyList<string> EnumerateMsBuildFiles(string repoRoot)
    {
        var files = new List<string>();
        void Walk(string dir)
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (MsBuildFileSkipDirectories.Contains(Path.GetFileName(sub), StringComparer.Ordinal))
                {
                    continue;
                }

                Walk(sub);
            }

            foreach (var pattern in new[] { "*.csproj", "*.props", "*.targets" })
            {
                files.AddRange(Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly));
            }
        }

        Walk(repoRoot);
        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    public static ModuleLedgerReport Scan(string srcRoot, ModuleLedger ledger)
    {
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)
            ?? throw new InvalidOperationException($"ModuleLedgerScanner: cannot derive a root from '{srcRoot}'.");

        var registryErrors = new List<string>(ledger.RegistryErrors);
        var namespaceOwners = BuildNamespaceIndex(ledger, registryErrors);
        var ownerKinds = ledger.Owners
            .GroupBy(o => o.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        ValidateEdgeCells(ledger, ownerKinds, registryErrors);
        var claimedTypeNamespaces = ledger.Owners.SelectMany(o => o.Types)
            .Where(t => t.Contains('.', StringComparison.Ordinal))
            .Select(t => t[..t.LastIndexOf('.')]).ToHashSet(StringComparer.Ordinal);

        var files = GuardScanner.EnumerateSourceFiles(srcRoot);

        var floor = IsRealRepo(srcFull) ? RealTreeFileFloor : files.Count;

        var parseErrors = new List<string>();
        var rawEdges = new List<CrossOwnerEdge>();
        var unowned = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var globalModuleImports = new List<GlobalModuleImport>();
        var scanned = new List<(SyntaxTree Tree, string Relative, string Project, List<Attribution> Attributions)>();

        foreach (var file in files)
        {
            var relative = Relative(repoRoot, file);
            var tree = CompatibilityExceptionScanner.Parse(file);
            var root = tree.GetCompilationUnitRoot();
            AddErrors(parseErrors, relative, tree.GetDiagnostics());
            var project = ProjectRootNamespace(srcFull, file);

            var attributions = ScanFile(root, relative, project, namespaceOwners, ownerKinds, claimedTypeNamespaces,
                unowned, globalModuleImports);
            scanned.Add((tree, relative, project, attributions));
        }

        AddStaleAssemblies(parseErrors, srcFull, repoRoot);

        // Only a module's references can be edges, so a file no module owns is never bound and
        // its compile errors cannot hide one.
        foreach (var project in scanned.GroupBy(s => s.Project))
        {
            var bound = project.Where(s => s.Attributions.Any(a => a.Owner is { } owner && !IsPlatform(ownerKinds, owner))).ToList();
            if (bound.Count == 0)
            {
                continue;
            }

            var compilation = CompatibilityExceptionScanner.Compile(project.Key, project.Select(s => s.Tree),
                CompatibilityExceptionScanner.ImplicitUsings, CompatibilityExceptionScanner.References(project.Key));
            var results = bound.AsParallel().AsOrdered().Select(file =>
            {
                var model = compilation.GetSemanticModel(file.Tree);
                var errors = new List<string>();
                var edges = new List<CrossOwnerEdge>();
                // A source generator implements a partial method (CS8795); the stub hides no reference.
                AddErrors(errors, file.Relative, model.GetDiagnostics().Where(d => d.Id != "CS8795"));

                RecordEdges(model, file.Tree.GetCompilationUnitRoot(), file.Relative, file.Attributions,
                    namespaceOwners, ownerKinds, edges);
                return (errors, edges);
            }).ToList();
            foreach (var (errors, edges) in results)
            {
                parseErrors.AddRange(errors);
                rawEdges.AddRange(edges);
            }
        }

        var liveEdges = Collapse(rawEdges);
        var declared = ledger.Edges
            .GroupBy(c => (c.From, c.To))
            .ToDictionary(g => g.Key, g => g.SelectMany(c => c.Symbols).ToHashSet(StringComparer.Ordinal));

        var undeclared = liveEdges
            .Where(e => !declared.TryGetValue((e.From, e.To), out var symbols) || !symbols.Contains(e.Symbol))
            .ToList();

        var realised = liveEdges
            .Select(e => (e.From, e.To, e.Symbol))
            .ToHashSet();

        var stale = new List<StaleLedgerSymbol>();
        foreach (var cell in ledger.Edges)
        {
            if (cell.Symbols.Count == 0)
            {
                stale.Add(new StaleLedgerSymbol(cell.From, cell.To, "<none>",
                    "the cell lists no symbols, so it excuses nothing and can never go stale — delete it"));
                continue;
            }

            foreach (var symbol in cell.Symbols.Where(s => !realised.Contains((cell.From, cell.To, s))))
            {
                stale.Add(new StaleLedgerSymbol(cell.From, cell.To, symbol,
                    "no reference in src/ realises this edge — delete the symbol, or restore the dependency it was written for"));
            }
        }

        return new ModuleLedgerReport(
            liveEdges,
            undeclared,
            stale.OrderBy(s => s.From, StringComparer.Ordinal)
                .ThenBy(s => s.To, StringComparer.Ordinal)
                .ThenBy(s => s.Symbol, StringComparer.Ordinal)
                .ToList(),
            unowned.Values.ToList(),
            globalModuleImports,
            parseErrors.Distinct(StringComparer.Ordinal).ToList(),
            registryErrors,
            files.Count,
            floor);
    }

    /// <summary>
    /// Evaluates a report as a build gate: no parse or compile errors, no registry errors,
    /// the file-count floor holds, no unowned namespace, no undeclared edge and
    /// no stale ledger row. Returns the failure messages (empty = pass).
    /// </summary>
    public static IReadOnlyList<string> Evaluate(ModuleLedgerReport report)
    {
        var failures = new List<string>();

        if (report.ParseErrors.Count > 0)
        {
            failures.Add($"scan produced {report.ParseErrors.Count} parse or compile error(s) — the walk cannot be trusted:\n  " +
                         string.Join("\n  ", report.ParseErrors.Take(10)));
        }

        if (report.RegistryErrors.Count > 0)
        {
            failures.Add($"module-ledger registry error(s): {report.RegistryErrors.Count} — the walk is sound but the ledger contradicts itself:\n  " +
                         string.Join("\n  ", report.RegistryErrors.Take(10)));
        }

        if (report.ScannedFileCount < report.ExpectedFileCountFloor)
        {
            failures.Add($"scanned {report.ScannedFileCount} files, expected at least {report.ExpectedFileCountFloor} — the walk saw less than it should");
        }

        failures.AddRange(report.UnownedNamespaces);

        failures.AddRange(report.GlobalModuleImports.Select(import =>
            $"global using of module namespace '{import.Namespace}' in {import.File}:{import.Line} — a global import hides every dependency on it from the syntax walks; import it per file instead"));

        foreach (var edge in report.UndeclaredEdges)
        {
            failures.Add(
                $"undeclared cross-owner edge {edge.From} -> {edge.To} from {edge.Symbol} " +
                $"(references {string.Join(", ", edge.ReferencedNamespaces)}) at {edge.File}:{edge.Line} — " +
                $"add this row to src/Cluckwork.Domain/Common/Architecture/ModuleEdges.cs, with a reason naming the port or type it calls:\n{RenderEdges([edge])}");
        }

        foreach (var row in report.StaleSymbols)
        {
            failures.Add($"stale ledger row {row.From} -> {row.To} :: {row.Symbol} — {row.Reason}");
        }

        return failures;
    }

    private static List<Attribution> ScanFile(
        CompilationUnitSyntax root,
        string relative,
        string rootNamespace,
        IReadOnlyDictionary<string, Claim> namespaceOwners,
        IReadOnlyDictionary<string, string> ownerKinds,
        IReadOnlySet<string> claimedTypeNamespaces,
        SortedDictionary<string, string> unowned,
        List<GlobalModuleImport> globalModuleImports)
    {
        var topLevelTypes = root.DescendantNodes()
            .Where(IsTypeDeclaration)
            .Where(n => !n.Ancestors().Any(IsTypeDeclaration))
            .ToList();

        var fileNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .FirstOrDefault() ?? rootNamespace;

        var attributions = new List<Attribution>();
        foreach (var type in topLevelTypes)
        {
            var declared = NamespaceOf(type, rootNamespace);
            var owner = Resolve(namespaceOwners, declared, declared: true);
            if (owner is null)
            {
                RecordUnowned(unowned, declared,
                    $"unowned namespace '{declared}' declared in {relative} — every namespace in src/ must be claimed by exactly one ledger owner");
            }

            // A file-local type is visible in its own file only, so two files may
            // declare the same name; the path keeps their rows apart.
            var symbol = IsFileLocal(type)
                ? $"{declared}.{Identifier(type)}@{relative}"
                : $"{declared}.{Identifier(type)}";
            // #1023: a claimed type belongs to its claimant, not to its namespace's owner.
            var claimant = namespaceOwners.TryGetValue(symbol, out var claim) ? claim.Owner : owner?.Owner;
            attributions.Add(new(type, symbol, claimant));
        }

        if (attributions.Count == 0)
        {
            var owner = Resolve(namespaceOwners, fileNamespace, declared: true);
            if (owner is null)
            {
                RecordUnowned(unowned, fileNamespace,
                    $"unowned namespace '{fileNamespace}' declared in {relative} — every namespace in src/ must be claimed by exactly one ledger owner");
            }

            attributions.Add(new(null, $"<file>:{relative}", owner?.Owner));
        }

        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(d => d.GlobalKeyword != default))
        {
            // A single-identifier import is a namespace name too. A single-identifier ALIAS
            // target may name a type instead, and a syntax walk cannot tell which, so it is
            // left alone.
            var names = directive.DescendantNodes()
                .Where(node => node is QualifiedNameSyntax or MemberAccessExpressionSyntax)
                .Where(IsOutermostDotted)
                .Select(DottedText)
                .OfType<string>()
                .ToList();
            if (directive.Alias is null && directive.NamespaceOrType is IdentifierNameSyntax single)
            {
                names.Add(single.Identifier.ValueText);
            }

            foreach (var dotted in names.Distinct(StringComparer.Ordinal))
            {
                var resolved = ResolveReferenced(namespaceOwners, dotted, fileNamespace);
                if (resolved is { } owner
                    && ownerKinds.TryGetValue(owner.Owner, out var kind)
                    && (kind == ModuleLedger.ModuleKind || IsRootAlias(directive, owner.Namespace)))
                {
                    globalModuleImports.Add(new GlobalModuleImport(owner.Namespace, relative, LineOf(directive)));
                }
                // #1023: the peer walk resolves a simple name through its own file's imports only, so a global
                // import would hide a claimed type from it.
                else if (claimedTypeNamespaces.Contains(dotted))
                {
                    globalModuleImports.Add(new GlobalModuleImport(dotted, relative, LineOf(directive)));
                }
            }
        }

        return attributions;
    }

    // Every type a node binds to, or the type declaring the member it binds to, charged to the
    // top-level type that holds the node. Using directives are skipped: an import is not a use.
    // Inferred generic arguments are not charged unless a name binds to them.
    private static void RecordEdges(
        SemanticModel model,
        CompilationUnitSyntax root,
        string relative,
        IReadOnlyList<Attribution> attributions,
        IReadOnlyDictionary<string, Claim> namespaceOwners,
        IReadOnlyDictionary<string, string> ownerKinds,
        List<CrossOwnerEdge> edges)
    {
        foreach (var node in root.DescendantNodes(n => n is not UsingDirectiveSyntax))
        {
            var enclosing = node.Ancestors().LastOrDefault(IsTypeDeclaration);
            var from = attributions.FirstOrDefault(a => a.Scope == enclosing) ?? attributions[0];
            if (from.Owner is null || IsPlatform(ownerKinds, from.Owner))
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
                    || Resolve(namespaceOwners, $"{outermost.ContainingNamespace.ToDisplayString()}.{outermost.Name}", declared: false) is not { } to
                    || to.Owner == from.Owner || IsPlatform(ownerKinds, to.Owner))
                {
                    continue;
                }

                edges.Add(new CrossOwnerEdge(from.Owner, to.Owner, from.Symbol, [to.Namespace], relative, LineOf(node)));
            }
        }
    }

    // GetSymbolInfo names what the source spells; the compiler also calls conversion operators,
    // collection-initializer Adds and the foreach enumerator pattern on its own.
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

    // A project is compiled from source against its siblings' built assemblies; an assembly older
    // than its own source can bind a member to the wrong type and still produce no diagnostic.
    private static void AddStaleAssemblies(List<string> errors, string srcFull, string repoRoot)
    {
        foreach (var project in Directory.GetDirectories(srcFull))
        {
            var assembly = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(project) + ".dll");
            var newest = GuardScanner.EnumerateSourceFiles(project).MaxBy(File.GetLastWriteTimeUtc);
            if (File.Exists(assembly) && newest is not null
                && File.GetLastWriteTimeUtc(newest) > File.GetLastWriteTimeUtc(assembly))
            {
                errors.Add($"{Path.GetFileName(assembly)} is older than {Relative(repoRoot, newest)} — " +
                    "the walk would bind stale metadata; rebuild the test project");
            }
        }
    }

    // A bound file's diagnostics repeat its parse errors; the report drops the copies.
    private static void AddErrors(List<string> errors, string relative, IEnumerable<Diagnostic> diagnostics) =>
        errors.AddRange(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{relative}:{LineOf(d.Location)}: {d.Id} {d.GetMessage()}"));

    private sealed record Attribution(SyntaxNode? Scope, string Symbol, string? Owner);

    private static void RecordUnowned(SortedDictionary<string, string> unowned, string key, string message)
    {
        if (!unowned.ContainsKey(key))
        {
            unowned[key] = message;
        }
    }

    private static IReadOnlyList<CrossOwnerEdge> Collapse(IEnumerable<CrossOwnerEdge> raw) =>
        raw.GroupBy(e => (e.From, e.To, e.Symbol))
            .Select(g =>
            {
                var first = g
                    .OrderBy(e => e.File, StringComparer.Ordinal)
                    .ThenBy(e => e.Line)
                    .First();
                return first with
                {
                    ReferencedNamespaces = g.SelectMany(e => e.ReferencedNamespaces)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList(),
                };
            })
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Symbol, StringComparer.Ordinal)
            .ToList();

    internal sealed record Claim(string Owner, bool Subtree);

    internal static Dictionary<string, Claim> BuildNamespaceIndex(ModuleLedger ledger, List<string> errors)
    {
        foreach (var duplicate in ledger.Owners.GroupBy(o => o.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"duplicate owner '{duplicate.Key}' — {duplicate.Count()} entries share the name, so which one claims its namespaces is undefined");
        }

        // #1023: a claimed type is an exact claim on its full name. Resolve probes the full name first, so the
        // claim outranks the owner of the type's namespace.
        var index = new Dictionary<string, Claim>(StringComparer.Ordinal);
        foreach (var claim in ledger.Owners
            .SelectMany(o => o.Namespaces.Select(n => (Owner: o.Name, Namespace: n, Subtree: true, Label: "namespace"))
                .Concat(o.ExactNamespaces.Select(n => (Owner: o.Name, Namespace: n, Subtree: false, Label: "namespace")))
                .Concat(o.Types.Select(t => (Owner: o.Name, Namespace: t, Subtree: false, Label: "type"))))
            .GroupBy(c => c.Namespace, StringComparer.Ordinal))
        {
            var claimants = claim.Select(c => c.Owner).Distinct(StringComparer.Ordinal).OrderBy(o => o, StringComparer.Ordinal).ToList();
            if (claim.Count() > 1)
            {
                var label = claim.Any(c => c.Label == "type") ? "type" : "namespace";
                errors.Add($"{label} '{claim.Key}' is claimed {claim.Count()} times ({string.Join(", ", claimants)}) — every {label} must be claimed by exactly one owner");
            }

            index[claim.Key] = new Claim(claimants[0], claim.First().Subtree);
        }

        return index;
    }

    private static void ValidateEdgeCells(
        ModuleLedger ledger, IReadOnlyDictionary<string, string> ownerKinds, List<string> errors)
    {
        foreach (var duplicate in ledger.Edges.GroupBy(e => (e.From, e.To)).Where(g => g.Count() > 1))
        {
            errors.Add($"duplicate edge cell {duplicate.Key.From} -> {duplicate.Key.To} — {duplicate.Count()} cells share it, so neither can go stale on its own; fold them into one");
        }

        foreach (var cell in ledger.Edges)
        {
            if (cell.From == cell.To)
            {
                errors.Add($"edge cell {cell.From} -> {cell.To} names one owner on both ends — a module referencing itself is not an edge, so this cell can only ever be stale");
            }

            foreach (var end in new[] { cell.From, cell.To }.Distinct(StringComparer.Ordinal))
            {
                if (!ownerKinds.ContainsKey(end))
                {
                    errors.Add($"edge cell {cell.From} -> {cell.To} references unknown owner '{end}'");
                }
                else if (ownerKinds[end] == ModuleLedger.PlatformKind)
                {
                    errors.Add($"edge cell {cell.From} -> {cell.To} names platform owner '{end}' — Platform is the free hub, so an edge touching it is never emitted and this cell can only ever be stale");
                }
            }

            foreach (var duplicate in cell.Symbols.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                errors.Add($"edge cell {cell.From} -> {cell.To} lists symbol '{duplicate.Key}' {duplicate.Count()} times");
            }
        }
    }

    // `global using D = Cluckwork.Domain;` names an exactly claimed root, and
    // `D.Sales.Customer` at a use site cannot be expanded by this walk, so the
    // alias would hide every module namespace below the root.
    private static bool IsRootAlias(UsingDirectiveSyntax directive, string resolvedNamespace) =>
        directive.Alias is not null
        && DottedText(directive.NamespaceOrType) == resolvedNamespace;

    private static bool IsPlatform(IReadOnlyDictionary<string, string> kinds, string owner) =>
        kinds.TryGetValue(owner, out var kind) && kind == ModuleLedger.PlatformKind;

    internal static (string Owner, string Namespace)? ResolveReferenced(
        IReadOnlyDictionary<string, Claim> index, string dotted, string fileNamespace)
    {
        if (dotted.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Resolve(index, dotted, declared: false);
        }

        var prefix = fileNamespace;
        while (true)
        {
            var candidate = $"{prefix}.{dotted}";
            var owner = Resolve(index, candidate, declared: false);
            if (owner is { } resolved && resolved.Namespace.Length > prefix.Length)
            {
                return resolved;
            }

            var cut = prefix.LastIndexOf('.');
            if (cut < 0)
            {
                return null;
            }

            prefix = prefix[..cut];
        }
    }

    internal static (string Owner, string Namespace)? Resolve(IReadOnlyDictionary<string, Claim> index, string dotted, bool declared)
    {
        var probe = dotted;
        while (true)
        {
            if (index.TryGetValue(probe, out var claim) && (claim.Subtree || !declared || probe == dotted))
            {
                return (claim.Owner, probe);
            }

            var cut = probe.LastIndexOf('.');
            if (cut < 0)
            {
                return null;
            }

            probe = probe[..cut];
        }
    }

    private static string RenderEdges(IReadOnlyList<CrossOwnerEdge> edges) => string.Join("\n", edges
        .GroupBy(e => (e.From, e.To))
        .OrderBy(g => g.Key.From, StringComparer.Ordinal)
        .ThenBy(g => g.Key.To, StringComparer.Ordinal)
        .Select(cell => $"[assembly: ModuleEdge({RealModuleLedger.Quote(cell.Key.From)}, {RealModuleLedger.Quote(cell.Key.To)}, \"R\", \"\", " +
            $"{string.Join(", ", cell.Select(e => e.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(RealModuleLedger.Quote))})]"));

    private static bool IsTypeDeclaration(SyntaxNode node) =>
        node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;

    private static string Identifier(SyntaxNode node) => node switch
    {
        TypeDeclarationSyntax type => GenericIdentifier(type.Identifier.ValueText, type.TypeParameterList),
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax @delegate => GenericIdentifier(@delegate.Identifier.ValueText, @delegate.TypeParameterList),
        _ => throw new InvalidOperationException($"ModuleLedgerScanner: {node.Kind()} is not a type declaration."),
    };

    // Arity only: type parameter names are not part of a generic type's identity.
    private static string GenericIdentifier(string identifier, TypeParameterListSyntax? parameters) =>
        parameters is null ? identifier : $"{identifier}<{new string(',', parameters.Parameters.Count - 1)}>";

    private static bool IsFileLocal(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Modifiers.Any(SyntaxKind.FileKeyword),
        DelegateDeclarationSyntax @delegate => @delegate.Modifiers.Any(SyntaxKind.FileKeyword),
        _ => false,
    };

    internal static string NamespaceOf(SyntaxNode node, string rootNamespace)
    {
        var parts = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .Reverse()
            .ToList();
        return parts.Count == 0 ? rootNamespace : string.Join(".", parts);
    }

    private static bool IsOutermostDotted(SyntaxNode node) => node.Parent switch
    {
        QualifiedNameSyntax qualified => qualified.Left != node,
        MemberAccessExpressionSyntax access => access.Expression != node,
        _ => true,
    };

    internal static string? DottedText(SyntaxNode? node) => node switch
    {
        GenericNameSyntax generic => generic.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => Join(DottedText(qualified.Left), qualified.Right.Identifier.ValueText),
        AliasQualifiedNameSyntax alias => DottedText(alias.Name),
        MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression)
            => Join(DottedText(access.Expression), access.Name.Identifier.ValueText),
        _ => null,
    };

    private static string? Join(string? left, string right) => left is null ? null : $"{left}.{right}";

    private static int LineOf(SyntaxNode node) => LineOf(node.GetLocation());

    private static int LineOf(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;

    internal static string ProjectRootNamespace(string srcFull, string file)
    {
        var relative = NormalizePath(Path.GetRelativePath(srcFull, file));
        var slash = relative.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? relative[..slash] : "<global>";
    }

    private static bool IsRealRepo(string srcRoot) =>
        GuardScanner.FindRepoRoot(AppContext.BaseDirectory) is string root
        && string.Equals(Path.GetFullPath(srcRoot), Path.Combine(root, "src"), StringComparison.Ordinal);

    private static string Relative(string repoRoot, string file) =>
        NormalizePath(Path.GetRelativePath(repoRoot, file));

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
