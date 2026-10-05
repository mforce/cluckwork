using System.Reflection;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common.Architecture;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.CodeAnalysis.CSharp;

namespace Cluckwork.Application.Tests.Architecture;

// The one module ledger every real-tree test reads (#859). Its rows are the RealModuleLedger.*.cs files, plus the
// owner and edge rows in src/Cluckwork.Domain/Common/Architecture and the [ModuleContract] types, which the
// module-edge analyzer reads too.
internal static partial class RealModuleLedger
{
    private static readonly ILookup<string, string> Contracts = new[]
        {
            typeof(ModuleOwnerAttribute).Assembly, typeof(IUnitOfWork).Assembly, typeof(AppDbContext).Assembly,
        }
        .SelectMany(a => a.GetTypes())
        .Select(t => (Type: t.FullName!, t.GetCustomAttribute<ModuleContractAttribute>()?.Owner))
        .Where(c => c.Owner is not null)
        .OrderBy(c => c.Type, StringComparer.Ordinal)
        .ToLookup(c => c.Owner!, c => c.Type, StringComparer.Ordinal);

    internal static readonly OwnerDefinition[] Owners = [.. typeof(ModuleOwnerAttribute).Assembly
        .GetCustomAttributes<ModuleOwnerAttribute>()
        .Select(o => new OwnerDefinition(o.Name, o.Kind, o.Namespaces, o.ExactNamespaces)
        {
            Contract = [.. Contracts[o.Name]],
            Implementations = o.Implementations,
            Seam = o.Seam,
            Types = o.Types,
        })];

    internal static readonly EdgeCell[] Edges = [.. typeof(ModuleEdgeAttribute).Assembly
        .GetCustomAttributes<ModuleEdgeAttribute>()
        .Select(e => new EdgeCell(e.From, e.To, e.Kind, e.Reason, e.Symbols))];

    // A mark the reflection above would drop.
    private static readonly string[] RuleErrors = [.. Contracts.Where(g => !Owners.Any(o => o.Name == g.Key))
        .SelectMany(g => g.Select(type => $"contract type '{type}' names owner '{g.Key}', which has no ModuleOwner row"))];

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
