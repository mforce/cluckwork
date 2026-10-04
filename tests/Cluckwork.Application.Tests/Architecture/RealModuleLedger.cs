namespace Cluckwork.Application.Tests.Architecture;

// The one module ledger every real-tree test reads (#859). Nothing else loads the real rules.
internal static partial class RealModuleLedger
{
    internal static ModuleLedger Value { get; } = ModuleLedger.Validate(ModuleLedger.Parse(
        Path.Combine(AppContext.BaseDirectory, "Architecture", "Data", "module-ledger.json")));
}
