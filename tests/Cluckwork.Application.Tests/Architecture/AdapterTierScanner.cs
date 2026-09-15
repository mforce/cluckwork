namespace Cluckwork.Application.Tests.Architecture;

using System.Text.Encodings.Web;
using System.Text.Json;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed record ToolType(string Name, string File, int Line);

public sealed record SurfaceCallSite(string Surface, string File, int Line);

public sealed record UndeclaredDefineConstant(string ProjectFile, string Symbol);

public sealed record AdapterTierReport(
    IReadOnlyList<ToolType> ToolTypeOutsideTier,
    IReadOnlyList<SurfaceCallSite> SurfaceWithoutTier,
    IReadOnlyList<AdapterTier> Dormant,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<string> RegistryErrors,
    int ScannedFileCount,
    int ExpectedFileCountFloor,
    IReadOnlyList<UndeclaredDefineConstant> UndeclaredDefineConstants);

public static class AdapterTierScanner
{
    internal const int RealTreeFileFloor = 400;

    private static readonly HashSet<string> ToolAttributes = new(StringComparer.Ordinal)
    {
        "McpServerToolType", "McpServerToolTypeAttribute",
    };

    public static AdapterTierReport Scan(string srcRoot, string ledgerPath)
    {
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)!;
        var ledger = ModuleLedger.Load(ledgerPath);
        var errors = new List<string>(ledger.RegistryErrors);

        var files = GuardScanner.EnumerateSourceFiles(srcFull);
        var floor = GuardScanner.FindRepoRoot(AppContext.BaseDirectory) is { } realRoot
            && srcFull == Path.Combine(realRoot, "src") ? RealTreeFileFloor : files.Count;

        var parseErrors = new List<string>();
        var toolTypes = new List<(string Namespace, ToolType Type)>();
        var surfaceCalls = new List<SurfaceCallSite>();

        var parsed = new List<(string Relative, CompilationUnitSyntax Root, string ProjectNamespace)>();
        foreach (var file in files)
        {
            var relative = Relative(repoRoot, file);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), ModuleLedgerScanner.ParseOptions, file);
            var root = tree.GetCompilationUnitRoot();

            foreach (var diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                parseErrors.Add($"{relative}:{line}: {diagnostic.Id} {diagnostic.GetMessage()}");
            }

            parsed.Add((relative, root, ModuleLedgerScanner.ProjectRootNamespace(srcFull, file)));
        }

        var globalAliases = CollectGlobalAliases(parsed);

        foreach (var (relative, root, projectNamespace) in parsed)
        {
            var localAliases = LocalAliasDirectives(root);
            globalAliases.TryGetValue(projectNamespace, out var projectAliasMap);

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!HasToolAttribute(type, localAliases, projectAliasMap))
                {
                    continue;
                }

                var ns = ModuleLedgerScanner.NamespaceOf(type, projectNamespace);
                var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                toolTypes.Add((ns, new ToolType(FullName(type, ns), relative, line)));
            }

            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (CalledName(call) is { } name && AdapterTier.KnownSurfaces.ContainsKey(name.Identifier.ValueText))
                {
                    var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    surfaceCalls.Add(new SurfaceCallSite(name.Identifier.ValueText, relative, line));
                }
            }
        }

        var outside = toolTypes
            .Where(t => !ledger.AdapterTiers.Any(tier => Under(t.Namespace, tier.Namespace)))
            .Select(t => t.Type)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var invokedSurfaces = surfaceCalls.Select(c => c.Surface).ToHashSet(StringComparer.Ordinal);
        var tierSurfaces = ledger.AdapterTiers.Select(t => t.Surface).ToHashSet(StringComparer.Ordinal);
        var withoutTier = surfaceCalls
            .Where(c => !tierSurfaces.Contains(c.Surface))
            .OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Line).ToList();
        var dormant = ledger.AdapterTiers
            .Where(tier => !invokedSurfaces.Contains(tier.Surface)
                && !toolTypes.Any(t => Under(t.Namespace, tier.Namespace)))
            .OrderBy(t => t.Namespace, StringComparer.Ordinal).ToList();

        var undeclaredConstants = ModuleLedgerScanner.UndeclaredDefineConstants(repoRoot)
            .Select(c => new UndeclaredDefineConstant(c.ProjectFile, c.Symbol)).ToList();

        return new AdapterTierReport(
            outside, withoutTier, dormant, parseErrors, errors, files.Count, floor, undeclaredConstants);
    }

    public static IReadOnlyList<string> Evaluate(AdapterTierReport report)
    {
        var failures = report.RegistryErrors.Select(e => $"adapter tier registry error: {e}").ToList();
        if (report.ParseErrors.Count > 0)
        {
            failures.Add("the walk cannot be trusted:\n" + string.Join("\n", report.ParseErrors));
        }
        if (report.ScannedFileCount < report.ExpectedFileCountFloor)
        {
            failures.Add($"scanned {report.ScannedFileCount} files, expected at least {report.ExpectedFileCountFloor}");
        }
        foreach (var constant in report.UndeclaredDefineConstants)
        {
            failures.Add($"the walk cannot be trusted: project {constant.ProjectFile} defines " +
                $"{constant.Symbol} (a Condition attribute does not exempt it), add it to ModuleLedgerScanner.ParseOptions");
        }
        foreach (var type in report.ToolTypeOutsideTier)
        {
            failures.Add($"tool type {type.Name} at {type.File}:{type.Line} is outside every adapterTiers " +
                "namespace; add a tier row or move the type");
        }
        foreach (var call in report.SurfaceWithoutTier)
        {
            failures.Add($"mapped surface {call.Surface} at {call.File}:{call.Line} has no adapterTiers row; " +
                "review and add the JSON row:\n" + RenderRow(call.Surface));
        }
        return failures;
    }

    private static readonly JsonSerializerOptions RowOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Rendered only for a call site whose surface is already a known key (see the
    // caller), so the lookup below the map's own value — never the separate constant.
    internal static string RenderRow(string surface) => JsonSerializer.Serialize(new
    {
        @namespace = "<the tool namespace>",
        privilege = AdapterTier.KnownSurfaces[surface],
        surface,
        reason = "<why this surface needs the privilege, with a citation>",
        reviewBy = "<#issue>",
    }, RowOptions);

    private sealed record AliasDirective(string Alias, string Target, BaseNamespaceDeclarationSyntax? Block);

    private static bool HasToolAttribute(
        TypeDeclarationSyntax type,
        IReadOnlyList<AliasDirective> localAliases,
        IReadOnlyDictionary<string, string>? projectAliases) =>
        type.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attr => IsToolAttributeName(LastIdentifier(attr.Name), type, localAliases, projectAliases));

    // Round 2: `using ToolMarker = ActualMarker;` where ActualMarker is itself an
    // alias was matched against ToolAttributes directly and never matched. Walk the
    // chain to a fixed point instead, local aliases first then global ones at each
    // hop, bounded by the alias count so `using A = B; using B = A;` terminates.
    private static bool IsToolAttributeName(
        string identifier,
        TypeDeclarationSyntax type,
        IReadOnlyList<AliasDirective> localAliases,
        IReadOnlyDictionary<string, string>? projectAliases)
    {
        var current = identifier;
        var remainingHops = localAliases.Count + (projectAliases?.Count ?? 0) + 1;
        for (var hop = 0; hop < remainingHops; hop++)
        {
            if (ToolAttributes.Contains(current))
            {
                return true;
            }

            if (ResolveAliasOnce(current, type, localAliases, projectAliases) is not { } next)
            {
                return false;
            }

            current = next;
        }

        return false;
    }

    private static string? ResolveAliasOnce(
        string identifier,
        TypeDeclarationSyntax type,
        IReadOnlyList<AliasDirective> localAliases,
        IReadOnlyDictionary<string, string>? projectAliases)
    {
        foreach (var alias in localAliases)
        {
            if (alias.Alias == identifier && (alias.Block is null || type.Ancestors().Contains(alias.Block)))
            {
                return alias.Target;
            }
        }

        return projectAliases is not null && projectAliases.TryGetValue(identifier, out var globalTarget)
            ? globalTarget
            : null;
    }

    private static IReadOnlyList<AliasDirective> LocalAliasDirectives(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(d => d.Alias is not null && d.GlobalKeyword == default)
            .Select(d => (Directive: d, Target: AliasTargetLastIdentifier(d.NamespaceOrType)))
            .Where(d => d.Target is not null)
            .Select(d => new AliasDirective(
                d.Directive.Alias!.Name.Identifier.ValueText, d.Target!, d.Directive.Parent as BaseNamespaceDeclarationSyntax))
            .ToList();

    private static Dictionary<string, Dictionary<string, string>> CollectGlobalAliases(
        IEnumerable<(string Relative, CompilationUnitSyntax Root, string ProjectNamespace)> parsed)
    {
        var byProject = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (_, root, project) in parsed)
        {
            foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (directive.GlobalKeyword == default || directive.Alias is null)
                {
                    continue;
                }

                if (AliasTargetLastIdentifier(directive.NamespaceOrType) is not { } target)
                {
                    continue;
                }

                if (!byProject.TryGetValue(project, out var aliases))
                {
                    byProject[project] = aliases = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                aliases[directive.Alias.Name.Identifier.ValueText] = target;
            }
        }

        return byProject;
    }

    private static string? AliasTargetLastIdentifier(TypeSyntax target) =>
        target is NameSyntax name ? LastIdentifier(name) : null;

    private static string LastIdentifier(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => LastIdentifier(alias.Name),
        _ => name.ToString(),
    };

    private static SimpleNameSyntax? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name,
        MemberBindingExpressionSyntax binding => binding.Name,
        SimpleNameSyntax simple => simple,
        _ => null,
    };

    private static string FullName(TypeDeclarationSyntax type, string ns)
    {
        var typeNames = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>()
            .Reverse().Select(t => t.Identifier.ValueText);
        return ns + "." + string.Join(".", typeNames);
    }

    private static bool Under(string name, string prefix) =>
        name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal);

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
}
