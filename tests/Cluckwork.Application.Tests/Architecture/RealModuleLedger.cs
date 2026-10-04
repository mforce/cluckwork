using Microsoft.CodeAnalysis.CSharp;

namespace Cluckwork.Application.Tests.Architecture;

// The one module ledger every real-tree test reads (#859). Its rows are the RealModuleLedger.*.cs files.
internal static partial class RealModuleLedger
{
    // Lazy, because the order of static field initializers across partial files is not defined; Build runs on
    // first access, after every row array is set.
    private static readonly Lazy<ModuleLedger> Built = new(Build);

    internal static ModuleLedger Value => Built.Value;

    private static ModuleLedger Build() => ModuleLedger.Validate(new ModuleLedger(Owners, Edges, [])
    {
        Tables = Tables,
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
