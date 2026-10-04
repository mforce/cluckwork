using System.Text;
using System.Text.Json;

namespace Cluckwork.Application.Tests.TenantBypass;

// #859 parity phase: the C# rows must equal the committed files exactly, in order. Deleted with the files.
public sealed class TenantRegistryParityTests
{
    [Fact]
    public void AllowListRows_SerialiseIdenticallyToTheJson()
    {
        Assert.Equal(40, BypassAllowList.Entries.Count);
        Assert.Equal(JsonSerializer.Serialize(BypassAllowList.Entries), JsonSerializer.Serialize(BypassAllowList.Rows));
    }

    [Fact]
    public void FilterFreeSetRows_AreByteIdenticalToTheTsv()
    {
        Assert.Equal(36, FilterFreeSetSites.All.Count);
        Assert.Equal(FilterFreeSetSites.All.Select(Bytes), FilterFreeSetSites.Rows.Select(Bytes));
    }

    private static byte[] Bytes(FilterFreeSetSite site) => Encoding.UTF8.GetBytes(site.Key + '\u0001' + site.Reason);
}
