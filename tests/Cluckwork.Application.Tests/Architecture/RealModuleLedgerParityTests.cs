using System.Text.Json;

namespace Cluckwork.Application.Tests.Architecture;

// #859 S2 parity phase: the C# rows must equal module-ledger.json, in order and to the character, before the
// accessor switches to them. Deleted with the JSON.
public sealed class RealModuleLedgerParityTests
{
    [Fact]
    public void CSharpRows_EqualTheJsonLedger()
    {
        var json = RealModuleLedger.Value;
        Assert.Empty(json.RegistryErrors);
        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                json.CompatibilityExceptions,
                json.AdapterTiers,
            }),
            JsonSerializer.Serialize(new
            {
                RealModuleLedger.CompatibilityExceptions,
                RealModuleLedger.AdapterTiers,
            }));
    }
}
