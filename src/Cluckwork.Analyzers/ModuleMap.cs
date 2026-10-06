using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Cluckwork.Analyzers;

// The [ModuleOwner] and [ModuleEdge] rows on Cluckwork.Domain's <Owner>ModuleRules classes, read from source when
// compiling Domain and from metadata everywhere else, so an edited row takes effect without rebuilding the analyzer.
internal sealed class ModuleMap
{
    internal const string RulesDirectory = "src/Cluckwork.Domain/Common/Architecture/Modules/";

    private const string Namespace = "Cluckwork.Domain.Common.Architecture.";

    // ModuleLedgerScanner.BuildNamespaceIndex: a namespace claim covers its subtree; an exact namespace covers its
    // own name only.
    private readonly Dictionary<string, (string Owner, bool Subtree)> _claims = new(StringComparer.Ordinal);
    private readonly HashSet<string> _platform = new(StringComparer.Ordinal);
    private readonly HashSet<(string From, string To, string Symbol)> _declared = [];

    internal ImmutableArray<(string From, string To, ImmutableArray<string> Symbols)> Edges { get; private set; }

    internal static ModuleMap? Read(Compilation compilation)
    {
        var domain = compilation.Assembly.Name == "Cluckwork.Domain"
            ? compilation.Assembly
            : compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(a => a.Name == "Cluckwork.Domain");
        if (domain is null)
        {
            return null;
        }

        var map = new ModuleMap();
        var edges = ImmutableArray.CreateBuilder<(string, string, ImmutableArray<string>)>();
        foreach (var attribute in TypesIn(domain.GlobalNamespace).SelectMany(type => type.GetAttributes()))
        {
            var args = attribute.ConstructorArguments;
            switch (attribute.AttributeClass?.ToDisplayString())
            {
                case Namespace + "ModuleOwnerAttribute":
                    var owner = (string)args[0].Value!;
                    if ((string?)args[1].Value == "platform")
                    {
                        map._platform.Add(owner);
                    }

                    foreach (var named in attribute.NamedArguments)
                    {
                        if (named.Key is "Namespaces" or "ExactNamespaces")
                        {
                            foreach (var claimed in named.Value.Values)
                            {
                                map._claims[(string)claimed.Value!] = (owner, named.Key == "Namespaces");
                            }
                        }
                    }

                    break;
                case Namespace + "ModuleEdgeAttribute":
                    var (from, to) = ((string)args[0].Value!, (string)args[1].Value!);
                    var symbols = args[4].Values.Select(v => (string)v.Value!).ToImmutableArray();
                    edges.Add((from, to, symbols));
                    foreach (var symbol in symbols)
                    {
                        map._declared.Add((from, to, symbol));
                    }

                    break;
            }
        }

        map.Edges = edges.ToImmutable();
        return map._claims.Count == 0 ? null : map;
    }

    private static IEnumerable<INamedTypeSymbol> TypesIn(INamespaceSymbol ns) =>
        ns.GetTypeMembers().Concat(ns.GetNamespaceMembers().SelectMany(TypesIn));

    internal static string RulesFile(string owner) => $"{RulesDirectory}{owner}.cs";

    internal bool IsPlatform(string owner) => _platform.Contains(owner);

    internal bool IsDeclared(string from, string to, string symbol) => _declared.Contains((from, to, symbol));

    internal string? Claimant(string symbol) => _claims.TryGetValue(symbol, out var claim) ? claim.Owner : null;

    // ModuleLedgerScanner.Resolve: the longest claimed prefix. Resolving a declared namespace, an exact claim covers
    // only its own name.
    internal (string Owner, string Namespace)? Resolve(string dotted, bool declared)
    {
        var probe = dotted;
        while (true)
        {
            if (_claims.TryGetValue(probe, out var claim) && (claim.Subtree || !declared || probe == dotted))
            {
                return (claim.Owner, probe);
            }

            var cut = probe.LastIndexOf('.');
            if (cut < 0)
            {
                return null;
            }

            probe = probe.Substring(0, cut);
        }
    }

    internal string Fix(string from, string to, string symbol) =>
        Edges.Any(cell => cell.From == from && cell.To == to)
            ? $"add \"{symbol}\" to the ModuleEdge(\"{from}\", \"{to}\", ...) row in {RulesFile(from)}, and extend its reason"
            : $"add this row to {RulesFile(from)}, with a reason naming the port or type it calls: [ModuleEdge(\"{from}\", \"{to}\", \"R\", \"\", \"{symbol}\")]";
}
