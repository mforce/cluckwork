namespace Cluckwork.Domain.Common.Architecture;

// The module map (#514, #842, #859): one owner row per module and one edge cell per cross-owner dependency,
// both on one <Owner>ModuleRules class in Modules/<Owner>.cs. A module's contract is its Modules/<Owner>/Contracts
// folder (#1087), so it needs no row here. Domain is the one assembly every module compilation references, so the
// module-edge analyzer reads these rows from source or metadata, and the architecture tests read them by
// reflection through RealModuleLedger. Owner names stay strings (#859).

[AttributeUsage(AttributeTargets.Class)]
public sealed class ModuleOwnerAttribute(string name, string kind) : Attribute
{
    public string Name { get; } = name;

    public string Kind { get; } = kind;

    public string[] Namespaces { get; set; } = [];

    public string[] ExactNamespaces { get; set; } = [];

    // #1023: non-contract types peer modules may still reach. Adapters may not, and the contract walk skips
    // them, because a seam can carry an aggregate on purpose (#851's account seam).
    public string[] Seam { get; set; } = [];

    // #1116: the owner is a read model over its peers' tables (Insights). CW1004 lets it name peer entities;
    // InsightsReadOnlyTests keeps it read-only and CompatibilityExceptionRealTreeTests keeps its reads declared.
    public bool ReadModel { get; set; }
}

// #846/#1116: the adapter roots. AdapterReachRealTreeTests walks them and CW1004 holds them to contracts. Every
// [AdapterTier] namespace joins them (Cluckwork.Analyzers.AdapterScope).
[AttributeUsage(AttributeTargets.Class)]
public sealed class AdapterRootsAttribute : Attribute
{
    public string[] Namespaces { get; set; } = [];

    public string[] Types { get; set; } = [];

    public string[] TopLevelPrograms { get; set; } = [];

    public string[] PersistenceForbiddenNamespaces { get; set; } = [];
}

// Symbols are the top-level from-side types realising the edge.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ModuleEdgeAttribute(string from, string to, string kind, string reason, params string[] symbols) : Attribute
{
    public string From { get; } = from;

    public string To { get; } = to;

    public string Kind { get; } = kind;

    public string Reason { get; } = reason;

    public string[] Symbols { get; } = symbols;
}

// #843: an adapter tier, declared before its surface exists. Privilege and Surface come from AdapterTier.KnownSurfaces
// in the architecture tests; ReviewBy is the issue that ends the exemption.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class AdapterTierAttribute(string ns, string privilege, string surface, string reason, string reviewBy) : Attribute
{
    public string Namespace { get; } = ns;

    public string Privilege { get; } = privilege;

    public string Surface { get; } = surface;

    public string Reason { get; } = reason;

    public string ReviewBy { get; } = reviewBy;
}
