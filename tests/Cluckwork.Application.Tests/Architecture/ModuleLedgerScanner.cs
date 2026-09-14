namespace Cluckwork.Application.Tests.Architecture;

// #842 (epic #514 slice 1) — the cross-owner edge ratchet over src/, read against Data/module-ledger.json.

using System.Text;
using System.Text.Json;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed record CrossOwnerEdge(
    string From,
    string To,
    string Symbol,
    IReadOnlyList<string> ReferencedNamespaces,
    string File,
    int Line);

public sealed record StaleLedgerSymbol(string From, string To, string Symbol, string Reason);

public sealed record ModuleLedgerReport(
    IReadOnlyList<CrossOwnerEdge> LiveEdges,
    IReadOnlyList<CrossOwnerEdge> UndeclaredEdges,
    IReadOnlyList<StaleLedgerSymbol> StaleSymbols,
    IReadOnlyList<string> UnownedNamespaces,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<string> RegistryErrors,
    int ScannedFileCount,
    int ExpectedFileCountFloor);

public static class ModuleLedgerScanner
{
    // Below the 466 files src/ held on 2026-09-14, so growth never reds the gate and a dropped subtree does.
    internal const int RealTreeFileFloor = 400;

    private const string Prefix = "Cluckwork.";

    public static ModuleLedgerReport Scan(string srcRoot, string ledgerPath)
    {
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)
            ?? throw new InvalidOperationException($"ModuleLedgerScanner: cannot derive a root from '{srcRoot}'.");

        var ledger = ModuleLedger.Load(ledgerPath);
        var registryErrors = new List<string>(ledger.RegistryErrors);
        var namespaceOwners = BuildNamespaceIndex(ledger, registryErrors);
        var ownerKinds = ledger.Owners
            .GroupBy(o => o.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        ValidateEdgeCells(ledger, ownerKinds, registryErrors);

        var files = GuardScanner.EnumerateSourceFiles(srcRoot);

        var floor = IsRealRepo(srcFull) ? RealTreeFileFloor : files.Count;

        var parseErrors = new List<string>();
        var rawEdges = new List<CrossOwnerEdge>();
        var unowned = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = Relative(repoRoot, file);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            var root = tree.GetCompilationUnitRoot();

            foreach (var diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                parseErrors.Add($"{relative}:{line}: {diagnostic.Id} {diagnostic.GetMessage()}");
            }

            ScanFile(root, relative, ProjectRootNamespace(srcFull, file), namespaceOwners, ownerKinds, rawEdges, unowned);
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
            parseErrors,
            registryErrors,
            files.Count,
            floor);
    }

    /// <summary>
    /// Evaluates a report as a build gate: no parse errors, no registry errors,
    /// the file-count floor holds, no unowned namespace, no undeclared edge and
    /// no stale ledger row. Returns the failure messages (empty = pass).
    /// </summary>
    public static IReadOnlyList<string> Evaluate(ModuleLedgerReport report)
    {
        var failures = new List<string>();

        if (report.ParseErrors.Count > 0)
        {
            failures.Add($"scan produced {report.ParseErrors.Count} parse error(s) — the walk cannot be trusted:\n  " +
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

        foreach (var edge in report.UndeclaredEdges)
        {
            failures.Add(
                $"undeclared cross-owner edge {edge.From} -> {edge.To} from {edge.Symbol} " +
                $"(references {string.Join(", ", edge.ReferencedNamespaces)}) at {edge.File}:{edge.Line} — " +
                $"add this to module-ledger.json, with a reason naming the port or type it calls:\n{RenderEdges([edge])}");
        }

        foreach (var row in report.StaleSymbols)
        {
            failures.Add($"stale ledger row {row.From} -> {row.To} :: {row.Symbol} — {row.Reason}");
        }

        return failures;
    }

    private static void ScanFile(
        CompilationUnitSyntax root,
        string relative,
        string rootNamespace,
        IReadOnlyDictionary<string, Claim> namespaceOwners,
        IReadOnlyDictionary<string, string> ownerKinds,
        List<CrossOwnerEdge> edges,
        SortedDictionary<string, string> unowned)
    {
        var topLevelTypes = root.DescendantNodes()
            .Where(IsTypeDeclaration)
            .Where(n => !n.Ancestors().Any(IsTypeDeclaration))
            .ToList();

        var fileNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .FirstOrDefault() ?? rootNamespace;

        var attributions = new List<(SyntaxNode? Scope, string Symbol, string? Owner)>();
        foreach (var type in topLevelTypes)
        {
            var declared = NamespaceOf(type, rootNamespace);
            var owner = Resolve(namespaceOwners, declared);
            if (owner is null)
            {
                RecordUnowned(unowned, declared,
                    $"unowned namespace '{declared}' declared in {relative} — every namespace in src/ must be claimed by exactly one ledger owner");
            }

            attributions.Add((type, $"{declared}.{Identifier(type)}", owner?.Owner));
        }

        if (attributions.Count == 0)
        {
            var owner = Resolve(namespaceOwners, fileNamespace);
            if (owner is null)
            {
                RecordUnowned(unowned, fileNamespace,
                    $"unowned namespace '{fileNamespace}' declared in {relative} — every namespace in src/ must be claimed by exactly one ledger owner");
            }

            attributions.Add((null, $"<file>:{relative}", owner?.Owner));
        }

        void Record(string dotted, int line, IEnumerable<(SyntaxNode? Scope, string Symbol, string? Owner)> targets)
        {
            if (!dotted.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return;
            }

            var to = Resolve(namespaceOwners, dotted);
            if (to is null)
            {
                RecordUnowned(unowned, dotted,
                    $"unowned namespace '{dotted}' referenced from {relative}:{line} — every referenced Cluckwork namespace must be claimed by exactly one ledger owner");
                return;
            }

            foreach (var target in targets)
            {
                if (target.Owner is null || target.Owner == to.Value.Owner)
                {
                    continue;
                }

                if (IsPlatform(ownerKinds, target.Owner) || IsPlatform(ownerKinds, to.Value.Owner))
                {
                    continue;
                }

                edges.Add(new CrossOwnerEdge(
                    target.Owner, to.Value.Owner, target.Symbol, [to.Value.Namespace], relative, line));
            }
        }

        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (DottedText(directive.NamespaceOrType) is string dotted)
            {
                Record(dotted, LineOf(directive), attributions);
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            if (node is not (QualifiedNameSyntax or MemberAccessExpressionSyntax)
                || !IsOutermostDotted(node)
                || node.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
            {
                continue;
            }

            if (DottedText(node) is not string dotted)
            {
                continue;
            }

            var enclosing = node.Ancestors().LastOrDefault(IsTypeDeclaration);
            var target = attributions.FirstOrDefault(a => a.Scope == enclosing);
            if (target.Symbol is null)
            {
                target = attributions[0];
            }

            Record(dotted, LineOf(node), [target]);
        }
    }

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

    private sealed record Claim(string Owner, bool Subtree);

    private static Dictionary<string, Claim> BuildNamespaceIndex(ModuleLedger ledger, List<string> errors)
    {
        foreach (var duplicate in ledger.Owners.GroupBy(o => o.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"duplicate owner '{duplicate.Key}' — {duplicate.Count()} entries share the name, so which one claims its namespaces is undefined");
        }

        var index = new Dictionary<string, Claim>(StringComparer.Ordinal);
        foreach (var claim in ledger.Owners
            .SelectMany(o => o.Namespaces.Select(n => (Owner: o.Name, Namespace: n, Subtree: true))
                .Concat(o.ExactNamespaces.Select(n => (Owner: o.Name, Namespace: n, Subtree: false))))
            .GroupBy(c => c.Namespace, StringComparer.Ordinal))
        {
            var claimants = claim.Select(c => c.Owner).Distinct(StringComparer.Ordinal).OrderBy(o => o, StringComparer.Ordinal).ToList();
            if (claim.Count() > 1)
            {
                errors.Add($"namespace '{claim.Key}' is claimed {claim.Count()} times ({string.Join(", ", claimants)}) — every namespace must be claimed by exactly one owner");
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

    private static bool IsPlatform(IReadOnlyDictionary<string, string> kinds, string owner) =>
        kinds.TryGetValue(owner, out var kind) && kind == ModuleLedger.PlatformKind;

    private static (string Owner, string Namespace)? Resolve(IReadOnlyDictionary<string, Claim> index, string dotted)
    {
        var probe = dotted;
        while (true)
        {
            if (index.TryGetValue(probe, out var claim) && (claim.Subtree || probe == dotted))
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

    private static string RenderEdges(IReadOnlyList<CrossOwnerEdge> edges)
    {
        var builder = new StringBuilder();
        var cells = edges
            .GroupBy(e => (e.From, e.To))
            .OrderBy(g => g.Key.From, StringComparer.Ordinal)
            .ThenBy(g => g.Key.To, StringComparer.Ordinal)
            .ToList();

        builder.Append("[\n");
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var symbols = cell.Select(e => e.Symbol).Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();

            builder.Append("  {\n");
            builder.Append($"    \"from\": {Quote(cell.Key.From)}, \"to\": {Quote(cell.Key.To)}, \"kind\": \"R\",\n");
            builder.Append("    \"reason\": \"\",\n");
            builder.Append("    \"symbols\": [\n");
            for (var s = 0; s < symbols.Count; s++)
            {
                builder.Append($"      {Quote(symbols[s])}{(s == symbols.Count - 1 ? string.Empty : ",")}\n");
            }

            builder.Append("    ]\n");
            builder.Append($"  }}{(i == cells.Count - 1 ? string.Empty : ",")}\n");
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static bool IsTypeDeclaration(SyntaxNode node) =>
        node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;

    private static string Identifier(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
        _ => throw new InvalidOperationException($"ModuleLedgerScanner: {node.Kind()} is not a type declaration."),
    };

    private static string NamespaceOf(SyntaxNode node, string rootNamespace)
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

    private static string? DottedText(SyntaxNode? node) => node switch
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

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string ProjectRootNamespace(string srcFull, string file)
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
