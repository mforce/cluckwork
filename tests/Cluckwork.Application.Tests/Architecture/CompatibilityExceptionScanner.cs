using System.Xml.Linq;
using Cluckwork.Application.Tests.TenantBypass;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cluckwork.Application.Tests.Architecture;

// #850 (epic #514 slice 8) — every DbSet read of a contracted module's table from outside that
// module is either allowed structurally or registered under `compatibilityExceptions`.

public sealed record DbSetRead(string Symbol, string Reaches, string Table, string Allowance, string File, int Line);

public sealed record CompatibilityExceptionReport(
    IReadOnlyList<DbSetRead> Reads,
    IReadOnlyList<DbSetRead> Undeclared,
    IReadOnlyList<DbSetRead> UnlistedTables,
    IReadOnlyList<string> Stale,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> CompileErrors,
    IReadOnlyList<string> RegistryErrors,
    int CompiledFileCount);

public static class CompatibilityExceptionScanner
{
    // Compiled with zero errors tolerated. Projects referencing it compile with errors tolerated.
    internal const string SemanticProject = "Cluckwork.Infrastructure";

    // Below the 156 files src/Cluckwork.Infrastructure held on 2026-10-02.
    internal const int RealTreeFileFloor = 125;

    internal const string Registered = "registered";
    internal const string OwnModule = "own module";
    internal const string DeclaredEdge = "declared edge";
    internal const string Implementation = "declared implementation";
    internal const string DbSetDeclaration = "DbSet declaration";

    private const string DbSetDefinition = "Microsoft.EntityFrameworkCore.DbSet<TEntity>";

    // The implicit usings of Microsoft.NET.Sdk, then the ones Microsoft.NET.Sdk.Web adds.
    internal const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private const string WebImplicitUsings = """
        global using System.Net.Http.Json;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    // Keeps type parameters, so ExpenseRepository<T> never shares ExpenseRepository's key, and
    // matches ClrKey, so IdentityUserRole<Guid> finds its model entry.
    private static readonly SymbolDisplayFormat TypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private static readonly Lazy<IModel> RealModel = new(() =>
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            .EnableServiceProviderCaching(false).Options;
        using var context = new AppDbContext(options, new TenantContext(), new FlockScope());
        return context.Model;
    });

    // Entity CLR type -> every table it maps to, from the real model, so a read is classified by the
    // table's owner rather than the entity's namespace (UserRoleAssignment is the counterexample).
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> EntityTables = new(() =>
        RealModel.Value.GetEntityTypes()
            .Where(e => !e.HasSharedClrType && !e.IsOwned())
            .ToDictionary(e => ClrKey(e.ClrType), e => QueriedTables(e).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal));

    // A query of a set also loads its owned values and, for a base type, its derived types,
    // and EF joins their tables when they are mapped apart from the principal's.
    internal static IEnumerable<string> QueriedTables(IEntityType entity) =>
        TableOwnerScanner.TableStoreObjects(entity).Select(t => TableOwnerScanner.Qualify(t.Name, t.Schema))
            .Concat(entity.GetNavigations().Where(n => n.ForeignKey.IsOwnership && !n.IsOnDependent)
                .SelectMany(n => QueriedTables(n.TargetEntityType)))
            .Concat(entity.GetDirectlyDerivedTypes().SelectMany(QueriedTables));

    public static CompatibilityExceptionReport Scan(string srcRoot, ModuleLedger ledger)
    {
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)!;
        var registryErrors = new List<string>(ledger.RegistryErrors);
        var index = ModuleLedgerScanner.BuildNamespaceIndex(ledger, registryErrors);
        var contracted = ledger.Owners.Where(o => o.Contract.Count > 0)
            .ToDictionary(o => o.Name, StringComparer.Ordinal);
        var tableOwners = TableOwnerScanner.Scan(RealModel.Value, ledger).Owners;
        ValidateRows(ledger, contracted, tableOwners, registryErrors);

        string? OwnerOf(INamespaceSymbol ns) =>
            ModuleLedgerScanner.Resolve(index, ns.ToDisplayString(), declared: true)?.Owner;

        var files = GuardScanner.EnumerateSourceFiles(Path.Combine(srcFull, SemanticProject));
        var compilation = Compile(SemanticProject, files.Select(Parse), ImplicitUsings, References(SemanticProject));
        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{Relative(repoRoot, d.Location.SourceTree?.FilePath ?? "")}:{Line(d.Location)}: {d.Id} {d.GetMessage()}")
            .ToList();
        var lenient = ProjectsReferencing(srcFull, SemanticProject)
            .Select(p => Compile(p, GuardScanner.EnumerateSourceFiles(Path.Combine(srcFull, p)).Select(Parse),
                ImplicitUsings + WebImplicitUsings, References(SemanticProject).Append(compilation.ToMetadataReference())))
            .ToList();

        var reads = new List<DbSetRead>();
        var unresolved = new List<string>();
        var guardedProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var current in lenient.Prepend(compilation))
        {
            foreach (var tree in current.SyntaxTrees.Where(t => t.FilePath.Length > 0))
            {
                var model = current.GetSemanticModel(tree);
                var file = Relative(repoRoot, tree.FilePath);
                foreach (var expression in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
                {
                    if (expression is not (MemberAccessExpressionSyntax or InvocationExpressionSyntax or IdentifierNameSyntax)
                        || model.GetTypeInfo(expression).Type is not INamedTypeSymbol { TypeArguments: [var entity] } type
                        || !IsDbSet(type))
                    {
                        continue;
                    }

                    var member = EnclosingMember(model, expression);
                    var symbol = member is INamedTypeSymbol named ? Key(named) : $"{Key(member.ContainingType)}.{member.Name}";
                    var location = $"{symbol} at {file}:{Line(expression.GetLocation())}";
                    if (entity is not INamedTypeSymbol namedEntity)
                    {
                        unresolved.Add($"{location} obtains DbSet<{entity.Name}> of a type parameter, so the walk " +
                            "cannot tell which table it reads; obtain the set with a concrete entity type");
                        continue;
                    }

                    if (!EntityTables.Value.TryGetValue(Key(namedEntity), out var tables))
                    {
                        unresolved.Add($"{location} obtains DbSet<{Key(namedEntity)}>, " +
                            "which maps to no table in AppDbContext's model");
                        continue;
                    }

                    foreach (var table in tables)
                    {
                        if (!tableOwners.TryGetValue(table, out var module) || !contracted.ContainsKey(module))
                        {
                            continue;
                        }

                        if (member is IPropertySymbol property && IsDbSet(property.Type))
                        {
                            guardedProperties.Add(property.Name);
                        }

                        reads.Add(new DbSetRead(symbol, module, table, Allowance(member, module, expression), file,
                            Line(expression.GetLocation())));
                    }
                }
            }
        }

        // A name a lenient project cannot bind might be a guarded read the walk cannot see.
        foreach (var current in lenient)
        {
            foreach (var tree in current.SyntaxTrees.Where(t => t.FilePath.Length > 0))
            {
                var model = current.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var candidate = node is GenericNameSyntax { Identifier.ValueText: "Set" }
                        || (node.Parent is MemberAccessExpressionSyntax access && access.Name == node
                            && guardedProperties.Contains(node.Identifier.ValueText));
                    if (candidate && model.GetSymbolInfo(node).Symbol is null)
                    {
                        unresolved.Add($"{SyntacticKey(node)} at {Relative(repoRoot, tree.FilePath)}:{Line(node.GetLocation())}: " +
                            $"'{node}' does not bind in {current.AssemblyName}, so it could be a DbSet read the walk cannot classify");
                    }
                }
            }
        }

        ValidateImplementations(contracted, compilation, reads, OwnerOf, registryErrors);

        var distinct = reads
            .GroupBy(r => (r.Symbol, r.Reaches, r.Table))
            .Select(g => g.OrderBy(r => r.Allowance == Registered ? 0 : 1)
                .ThenBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).First())
            .OrderBy(r => r.Symbol, StringComparer.Ordinal).ThenBy(r => r.Reaches, StringComparer.Ordinal)
            .ThenBy(r => r.Table, StringComparer.Ordinal)
            .ToList();
        var registered = distinct.Where(r => r.Allowance == Registered).ToList();
        var rows = ledger.CompatibilityExceptions.GroupBy(e => (e.Symbol, e.Reaches))
            .ToDictionary(g => g.Key, g => g.First());
        var undeclared = registered.Where(r => !rows.ContainsKey((r.Symbol, r.Reaches))).ToList();
        var unlisted = registered.Where(r => rows.TryGetValue((r.Symbol, r.Reaches), out var row)
            && !row.Tables.Contains(r.Table, StringComparer.Ordinal)).ToList();

        var stale = new List<string>();
        foreach (var row in ledger.CompatibilityExceptions)
        {
            var read = registered.Where(r => r.Symbol == row.Symbol && r.Reaches == row.Reaches)
                .Select(r => r.Table).ToHashSet(StringComparer.Ordinal);
            if (read.Count == 0)
            {
                stale.Add($"stale compatibility exception {row.Symbol} -> {row.Reaches}: no member by that key reads " +
                    $"{row.Reaches}'s tables any more, so delete the row (its trigger was {row.DeleteWhen})");
                continue;
            }

            stale.AddRange(row.Tables.Where(t => !read.Contains(t)).Select(t =>
                $"stale table '{t}' in compatibility exception {row.Symbol} -> {row.Reaches}: the member no longer reads it"));
        }

        return new CompatibilityExceptionReport(distinct, undeclared, unlisted, stale, unresolved.Distinct().ToList(), compileErrors,
            registryErrors, files.Count);

        string Allowance(ISymbol member, string module, ExpressionSyntax expression)
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

            if (ledger.Edges.Any(e => e.From == owner && e.To == module && e.Symbols.Contains(Key(outermost), StringComparer.Ordinal)))
            {
                return DeclaredEdge;
            }

            if (contracted[module].Implementations.Contains(Key(type), StringComparer.Ordinal))
            {
                return Implementation;
            }

            return member is IPropertySymbol property && IsDbSet(property.Type) && DerivesFromDbContext(type)
                && expression is InvocationExpressionSyntax { Expression: GenericNameSyntax { Identifier.ValueText: "Set" }, ArgumentList.Arguments.Count: 0 }
                && expression.Parent is ArrowExpressionClauseSyntax
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
        failures.AddRange(report.Unresolved.Select(e => $"unresolved: {e}"));
        failures.AddRange(report.Undeclared.GroupBy(r => (r.Symbol, r.Reaches)).Select(g =>
            $"undeclared compatibility exception {g.Key.Symbol} -> {g.Key.Reaches} at {g.First().File}:{g.First().Line}. " +
            "Read through the module's contract, or add this row to RealModuleLedger.CompatibilityExceptions and fill in " +
            $"owner, reason and deleteWhen:\nnew({RealModuleLedger.Quote(g.Key.Symbol)}, {RealModuleLedger.Quote(g.Key.Reaches)}, " +
            $"{RealModuleLedger.List(g.Select(r => r.Table))}, \"\", \"\", \"#\"),"));
        failures.AddRange(report.UnlistedTables.Select(r =>
            $"compatibility exception {r.Symbol} -> {r.Reaches} reads table '{r.Table}' at {r.File}:{r.Line}, which its " +
            "row does not name. Read it through the contract, or add the table and say why in the row's reason"));
        failures.AddRange(report.Stale);
        return failures;
    }

    private static void ValidateRows(ModuleLedger ledger, IReadOnlyDictionary<string, OwnerDefinition> contracted,
        IReadOnlyDictionary<string, string> tableOwners, List<string> errors)
    {
        var owners = ledger.Owners.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var row in ledger.CompatibilityExceptions)
        {
            if (!string.IsNullOrWhiteSpace(row.Reaches) && !contracted.ContainsKey(row.Reaches))
            {
                errors.Add($"compatibilityExceptions row '{row.Symbol}' reaches '{row.Reaches}', which declares no " +
                    "contract — only a contracted module's tables are guarded");
            }

            if (!string.IsNullOrWhiteSpace(row.Owner) && !owners.Contains(row.Owner))
            {
                errors.Add($"compatibilityExceptions row '{row.Symbol}' has unknown owner '{row.Owner}'");
            }

            foreach (var table in row.Tables.Where(t => tableOwners.GetValueOrDefault(t) != row.Reaches))
            {
                errors.Add($"compatibilityExceptions row '{row.Symbol}' names table '{table}', whose table owner " +
                    $"is not {row.Reaches}");
            }
        }

        foreach (var owner in ledger.Owners.Where(o => o.Implementations.Count > 0 && o.Contract.Count == 0))
        {
            errors.Add($"owner '{owner.Name}' lists implementations but declares no contract, so its tables are not guarded");
        }
    }

    private static void ValidateImplementations(IReadOnlyDictionary<string, OwnerDefinition> contracted,
        Compilation compilation, IReadOnlyList<DbSetRead> reads, Func<INamespaceSymbol, string?> ownerOf, List<string> errors)
    {
        var declared = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>()
            .GroupBy(Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (module, owner) in contracted)
        {
            foreach (var implementation in owner.Implementations)
            {
                if (!declared.TryGetValue(implementation, out var type))
                {
                    errors.Add($"owner '{module}' lists implementation '{implementation}', which is not declared in {SemanticProject}");
                }
                else if (!type.AllInterfaces.Any(i => ownerOf(i.ContainingNamespace) == module))
                {
                    errors.Add($"owner '{module}' lists implementation '{implementation}', which implements none of {module}'s interfaces");
                }
                else if (!reads.Any(r => r.Reaches == module && r.Allowance == Implementation
                             && r.Symbol.StartsWith(implementation + ".", StringComparison.Ordinal)))
                {
                    errors.Add($"owner '{module}' lists implementation '{implementation}', which reads none of {module}'s tables — remove it");
                }
            }
        }
    }

    internal static SyntaxTree Parse(string file) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(file), ModuleLedgerScanner.ParseOptions, file);

    internal static CSharpCompilation Compile(string assembly, IEnumerable<SyntaxTree> trees, string implicitUsings,
        IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create(assembly,
            trees.Append(CSharpSyntaxTree.ParseText(implicitUsings, ModuleLedgerScanner.ParseOptions)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

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

    private static bool IsDbSet(ITypeSymbol type) => type.OriginalDefinition.ToDisplayString() == DbSetDefinition;

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

    internal static IEnumerable<MetadataReference> References(string compiled)
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return platform.Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            .Where(p => Path.GetFileNameWithoutExtension(p) != compiled)
            .Distinct(StringComparer.Ordinal)
            .Select(p => MetadataReference.CreateFromFile(p));
    }

    private static string Key(INamedTypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static string ClrKey(Type type) => type.IsGenericType
        ? $"{(type.DeclaringType is { } outer ? ClrKey(outer) : type.Namespace)}.{type.Name[..type.Name.IndexOf('`')]}" +
          $"<{string.Join(", ", type.GetGenericArguments().Select(ClrKey))}>"
        : type.FullName!.Replace('+', '.');

    private static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
}
