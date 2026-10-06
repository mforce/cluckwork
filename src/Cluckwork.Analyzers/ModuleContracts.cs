namespace Cluckwork.Analyzers;

// #1087/#1116: the one contract rule, called by RealModuleLedger.DeriveContracts and by CW1004. A contract type sits
// in Cluckwork.{Domain,Application}.Modules.<Owner>.Contracts: a top-level type, or a type nested in one through public
// types only, such as a result record's cases. A nested type behind a private or internal level is not contract.
public static class ModuleContracts
{
    private const string Suffix = ".Contracts";

    // ns is the outermost type's namespace; hidden says a nesting level is not public.
    public static string? OwnerOf(string? ns, bool hidden)
    {
        if (hidden || ns is null || !ns.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var layer in new[] { "Cluckwork.Domain.Modules.", "Cluckwork.Application.Modules." })
        {
            if (ns.Length > layer.Length + Suffix.Length && ns.StartsWith(layer, StringComparison.Ordinal))
            {
                var owner = ns.Substring(layer.Length, ns.Length - layer.Length - Suffix.Length);
                return owner.IndexOf('.') < 0 ? owner : null;
            }
        }

        return null;
    }
}

// #843/#1116: every adapter tier's namespace joins the [AdapterRoots] namespaces and their persistence ban.
// AdapterReachScanner, AdapterTierScanner and CW1004 all read this union.
public static class AdapterScope
{
    public static IEnumerable<string> Namespaces(IEnumerable<string> roots, IEnumerable<string> tiers) => roots.Concat(tiers);

    public static IEnumerable<string> PersistenceForbidden(IEnumerable<string> forbidden, IEnumerable<string> tiers) =>
        forbidden.Concat(tiers);
}
