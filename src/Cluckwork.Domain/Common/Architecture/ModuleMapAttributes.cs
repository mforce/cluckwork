namespace Cluckwork.Domain.Common.Architecture;

// The module map (#514, #842, #859): one owner row per module and one edge cell per cross-owner dependency,
// declared in ModuleOwners.cs and ModuleEdges.cs, and [ModuleContract] on each contract type. Domain is the one assembly every module compilation
// references, so the module-edge analyzer reads these rows from source or metadata, and the architecture tests
// read them by reflection through RealModuleLedger. Owner names stay strings (#859).

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ModuleOwnerAttribute(string name, string kind) : Attribute
{
    public string Name { get; } = name;

    public string Kind { get; } = kind;

    public string[] Namespaces { get; set; } = [];

    public string[] ExactNamespaces { get; set; } = [];

    // #850: types outside the module's namespaces trusted to read its tables.
    public string[] Implementations { get; set; } = [];

    // #1023: non-contract types peer modules may still reach. Adapters may not, and the contract walk skips
    // them, because a seam can carry an aggregate on purpose (#851's account seam).
    public string[] Seam { get; set; } = [];

    // #1023: types this owner claims inside a Platform namespace.
    public string[] Types { get; set; } = [];
}

// #849: adapters may reach a contracted owner only through the types marked with its name.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, Inherited = false)]
public sealed class ModuleContractAttribute(string owner) : Attribute
{
    public string Owner { get; } = owner;
}

// Symbols are the top-level from-side types realising the edge.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ModuleEdgeAttribute(string from, string to, string kind, string reason, params string[] symbols) : Attribute
{
    public string From { get; } = from;

    public string To { get; } = to;

    public string Kind { get; } = kind;

    public string Reason { get; } = reason;

    public string[] Symbols { get; } = symbols;
}
