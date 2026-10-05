using System.Reflection;
using System.Text.RegularExpressions;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common.Architecture;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.CodeAnalysis.CSharp;

namespace Cluckwork.Application.Tests.Architecture;

// The one module ledger every real-tree test reads (#859). Its rows are the RealModuleLedger.*.cs files, plus the
// owner and edge rows on the <Owner>ModuleRules classes in src/Cluckwork.Domain/Common/Architecture/Modules, which the
// module-edge analyzer reads too, and the contract types (DeriveContracts).
internal static partial class RealModuleLedger
{
    // Design 3.4's order, which the coupling matrix's rows and columns follow; an unlisted owner sorts last.
    private static readonly string[] OwnerOrder =
        ["Access", "Farm", "FlockManagement", "EggOperations", "Commerce", "GeneralInventory", "Finance", "Insights", "Platform"];

    private static readonly Type[] RuleTypes =
        [.. typeof(ModuleOwnerAttribute).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(ModuleOwnerAttribute)) || t.IsDefined(typeof(ModuleEdgeAttribute)))];

    // Declared before Contracts: static initializers run in textual order.
    private static readonly Regex ContractsNamespace =
        new(@"^Cluckwork\.(?:Domain|Application)\.Modules\.(?<owner>[^.]+)\.Contracts$", RegexOptions.CultureInvariant);

    private static readonly (ILookup<string, string> Types, string[] Errors) Contracts = DeriveContracts(new[]
        {
            typeof(ModuleOwnerAttribute).Assembly, typeof(IUnitOfWork).Assembly,
            typeof(AppDbContext).Assembly,
        }
        .SelectMany(a => a.GetTypes()));

    // A contract type carries [ModuleContract] or, since #1087, sits top-level in
    // Cluckwork.{Domain,Application}.Modules.<Owner>.Contracts. A nested type never inherits its parent's status.
    // While both exist a type may carry only one: its module slice deletes the mark when it moves the type.
    internal static (ILookup<string, string> Types, string[] Errors) DeriveContracts(IEnumerable<Type> types)
    {
        var contracts = types
            .Select(t => (Type: t.FullName!,
                Mark: t.GetCustomAttribute<ModuleContractAttribute>()?.Owner,
                Folder: t.IsNested ? null : ContractsNamespace.Match(t.Namespace ?? "") is { Success: true } m
                    ? m.Groups["owner"].Value
                    : null))
            .Where(c => c.Mark is not null || c.Folder is not null)
            .OrderBy(c => c.Type, StringComparer.Ordinal)
            .ToList();
        return (contracts.ToLookup(c => (c.Mark ?? c.Folder)!, c => c.Type, StringComparer.Ordinal),
            [.. contracts.Where(c => c.Mark is not null && c.Folder is not null)
                .Select(c => $"contract type '{c.Type}' sits in a Contracts namespace and also carries [ModuleContract]; delete the mark")]);
    }

    internal static readonly OwnerDefinition[] Owners = [.. RuleTypes
        .Select(t => t.GetCustomAttribute<ModuleOwnerAttribute>()).OfType<ModuleOwnerAttribute>()
        .OrderBy(o => (uint)Array.IndexOf(OwnerOrder, o.Name))
        .Select(o => new OwnerDefinition(o.Name, o.Kind, o.Namespaces, o.ExactNamespaces)
        {
            Contract = [.. Contracts.Types[o.Name]],
            Implementations = o.Implementations,
            Seam = o.Seam,
            Types = o.Types,
        })];

    internal static readonly EdgeCell[] Edges = [.. RuleTypes
        .SelectMany(t => t.GetCustomAttributes<ModuleEdgeAttribute>())
        .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
        .Select(e => new EdgeCell(e.From, e.To, e.Kind, e.Reason, e.Symbols))];

    // A row the reflection above would drop or misfile.
    private static readonly string[] RuleErrors =
    [
        .. Contracts.Errors,
        .. Contracts.Types.Where(g => !Owners.Any(o => o.Name == g.Key))
            .SelectMany(g => g.Select(type => $"contract type '{type}' names owner '{g.Key}', which has no ModuleOwner row")),
        .. RuleTypes.SelectMany(t => t.GetCustomAttributes<ModuleEdgeAttribute>()
            .Where(e => e.From != t.GetCustomAttribute<ModuleOwnerAttribute>()?.Name)
            .Select(e => $"edge {e.From} -> {e.To} sits on {t.Name}; move it to {e.From}'s rules class")),
    ];

    // Lazy, because the order of static field initializers across partial files is not defined; Build runs on
    // first access, after every row array is set.
    private static readonly Lazy<ModuleLedger> Built = new(Build);

    internal static ModuleLedger Value => Built.Value;

    private static ModuleLedger Build() => ModuleLedger.Validate(new ModuleLedger(Owners, Edges, RuleErrors)
    {
        ForeignKeys = ForeignKeys,
        TableOwnerOverrides = TableOwnerOverrides,
        AdapterRoots = AdapterRoots,
        Adapters = Adapters,
        AdapterTiers = AdapterTiers,
        CompatibilityExceptions = CompatibilityExceptions,
    });

    // The "add this row" diagnostics print rows the way these files write them.
    internal static string Quote(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    internal static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";
}
