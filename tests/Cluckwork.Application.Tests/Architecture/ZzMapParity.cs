using System.Reflection;
using System.Text.Json;
using Cluckwork.Domain.Common.Architecture;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class ZzMapParity
{
    [Fact]
    public void AttributesEqualRows()
    {
        var domain = typeof(ModuleOwnerAttribute).Assembly;
        var owners = domain.GetCustomAttributes<ModuleOwnerAttribute>().Select(o =>
            new OwnerDefinition(o.Name, o.Kind, o.Namespaces, o.ExactNamespaces)
            { Contract = o.Contract, Implementations = o.Implementations, Seam = o.Seam, Types = o.Types }).ToArray();
        var edges = domain.GetCustomAttributes<ModuleEdgeAttribute>().Select(e =>
            new EdgeCell(e.From, e.To, e.Kind, e.Reason, e.Symbols)).ToArray();
        Assert.Equal(JsonSerializer.Serialize(RealModuleLedger.Owners), JsonSerializer.Serialize(owners));
        Assert.Equal(JsonSerializer.Serialize(RealModuleLedger.Edges), JsonSerializer.Serialize(edges));
        Assert.Equal(9, owners.Length);
        Assert.Equal(RealModuleLedger.Edges.Length, edges.Length);
    }
}
