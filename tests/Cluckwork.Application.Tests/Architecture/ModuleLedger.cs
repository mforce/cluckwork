using System.Text.RegularExpressions;
using Cluckwork.Analyzers;

namespace Cluckwork.Application.Tests.Architecture;

// #842 — the committed module ledger: owners by namespace, one cell per cross-owner dependency.


public sealed record OwnerDefinition(
    string Name,
    string Kind,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string> ExactNamespaces)
{
    // #849: when non-empty, adapters may reach this owner only through these types.
    public IReadOnlyList<string> Contract { get; init; } = [];

    // #1023: non-contract types peer modules may still reach. Adapters may not, and the contract walk skips
    // them, because a seam can carry an aggregate on purpose (#851's account seam).
    public IReadOnlyList<string> Seam { get; init; } = [];
}

public sealed record EdgeCell(string From, string To, string Kind, string Reason, IReadOnlyList<string> Symbols);

public sealed record ForeignKeyCell(string Table, string Name, string From, string To, string Reason);

public sealed record TableOwnerOverride(string Table, string Owner, string Reason);

public sealed record AdapterRoots(IReadOnlyList<string> Namespaces, IReadOnlyList<string> Types)
{
    public IReadOnlyList<string> TopLevelPrograms { get; init; } = [];
    public IReadOnlyList<string> PersistenceForbiddenNamespaces { get; init; } = [];
}

public sealed record AdapterClaim(string Symbol, IReadOnlyList<string> Reaches);

public sealed record AdapterTier(string Namespace, string Privilege, string Surface, string Reason, string ReviewBy)
{
    public const string DirectRepositoryPrivilege = "DirectRepository";

    // The closed set of surfaces a tier row may name, each mapped to the
    // privilege it grants. AdapterTierScanner reads this same map, so a surface
    // the scanner does not walk can never validate as a tier row's `surface`.
    public static readonly IReadOnlyDictionary<string, string> KnownSurfaces =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MapMcp"] = DirectRepositoryPrivilege,
        };
}

// #850: a member outside a contracted module that reads the module's tables through a DbSet.
public sealed record CompatibilityException(
    string Symbol, string Reaches, IReadOnlyList<string> Tables, string Owner, string Reason, string DeleteWhen);

public sealed record ModuleLedger(
    IReadOnlyList<OwnerDefinition> Owners,
    IReadOnlyList<EdgeCell> Edges,
    IReadOnlyList<string> RegistryErrors)
{
    public IReadOnlyList<ForeignKeyCell> ForeignKeys { get; init; } = [];
    public IReadOnlyList<TableOwnerOverride> TableOwnerOverrides { get; init; } = [];

    public AdapterRoots AdapterRoots { get; init; } = new([], []);
    public IReadOnlyList<AdapterClaim> Adapters { get; init; } = [];
    public IReadOnlyList<AdapterTier> AdapterTiers { get; init; } = [];
    public IReadOnlyList<CompatibilityException> CompatibilityExceptions { get; init; } = [];

    public IEnumerable<string> AdapterNamespaces =>
        AdapterScope.Namespaces(AdapterRoots.Namespaces, AdapterTiers.Select(t => t.Namespace));

    public IEnumerable<string> PersistenceForbiddenNamespaces =>
        AdapterScope.PersistenceForbidden(AdapterRoots.PersistenceForbiddenNamespaces, AdapterTiers.Select(t => t.Namespace));

    public const string ModuleKind = "module";
    public const string PlatformKind = "platform";

    private static readonly Regex IssuePattern = new("^#[0-9]+$", RegexOptions.Compiled);

    // Every rule about the ledger's values. Returns the ledger with its errors appended; the records are not
    // changed.
    public static ModuleLedger Validate(ModuleLedger ledger)
    {
        var errors = new List<string>(ledger.RegistryErrors);

        foreach (var owner in ledger.Owners)
            ValidateOwner(owner, errors);

        for (var index = 0; index < ledger.Edges.Count; index++)
            ValidateEdge(ledger.Edges[index], index, errors);

        for (var index = 0; index < ledger.ForeignKeys.Count; index++)
        {
            var (fk, label) = (ledger.ForeignKeys[index], $"foreignKeys[{index}]");
            Required(fk.Table, "table", label, errors);
            Required(fk.Name, "name", label, errors);
            Required(fk.From, "from", label, errors);
            Required(fk.To, "to", label, errors);
            Required(fk.Reason, "reason", label, errors);
        }

        for (var index = 0; index < ledger.TableOwnerOverrides.Count; index++)
        {
            var (row, label) = (ledger.TableOwnerOverrides[index], $"tableOwnerOverrides[{index}]");
            Required(row.Table, "table", label, errors);
            Required(row.Owner, "owner", label, errors);
            Required(row.Reason, "reason", label, errors);
        }

        NonBlank(ledger.AdapterRoots.Namespaces, "namespaces", "adapterRoots", errors);
        NonBlank(ledger.AdapterRoots.Types, "types", "adapterRoots", errors);
        NonBlank(ledger.AdapterRoots.TopLevelPrograms, "topLevelPrograms", "adapterRoots", errors);
        NonBlank(ledger.AdapterRoots.PersistenceForbiddenNamespaces, "persistenceForbiddenNamespaces", "adapterRoots", errors);

        for (var index = 0; index < ledger.Adapters.Count; index++)
        {
            var (row, label) = (ledger.Adapters[index], $"adapters[{index}]");
            Required(row.Symbol, "symbol", label, errors);
            NonBlank(row.Reaches, "reaches", label, errors);
        }

        for (var index = 0; index < ledger.AdapterTiers.Count; index++)
            ValidateAdapterTier(ledger.AdapterTiers[index], $"adapterTiers[{index}]", errors);
        foreach (var duplicate in ledger.AdapterTiers.GroupBy(t => t.Namespace, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key)))
        {
            errors.Add($"duplicate adapterTiers namespace '{duplicate.Key}'");
        }
        foreach (var duplicate in ledger.AdapterTiers.GroupBy(t => t.Surface, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key)))
        {
            errors.Add($"duplicate adapterTiers surface '{duplicate.Key}'");
        }

        for (var index = 0; index < ledger.CompatibilityExceptions.Count; index++)
            ValidateCompatibilityException(ledger.CompatibilityExceptions[index], $"compatibilityExceptions[{index}]", errors);
        foreach (var duplicate in ledger.CompatibilityExceptions.GroupBy(e => (e.Symbol, e.Reaches))
                     .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key.Symbol)))
        {
            errors.Add($"duplicate compatibilityExceptions row '{duplicate.Key.Symbol}' -> {duplicate.Key.Reaches}");
        }

        return ledger with { RegistryErrors = errors };
    }

    private static void Required(string value, string name, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{label} has a blank or non-string '{name}'");
    }

    private static void ValidateAdapterTier(AdapterTier tier, string label, List<string> errors)
    {
        var (privilege, surface, reviewBy) = (tier.Privilege, tier.Surface, tier.ReviewBy);
        Required(tier.Namespace, "namespace", label, errors);
        Required(privilege, "privilege", label, errors);
        Required(surface, "surface", label, errors);
        Required(tier.Reason, "reason", label, errors);
        Required(reviewBy, "reviewBy", label, errors);

        var allowedPrivileges = AdapterTier.KnownSurfaces.Values.Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(privilege) && !allowedPrivileges.Contains(privilege, StringComparer.Ordinal))
        {
            errors.Add($"{label} has privilege '{privilege}', which is not in the closed set " +
                $"{{{string.Join(", ", allowedPrivileges.Select(p => $"'{p}'"))}}}");
        }

        if (!string.IsNullOrWhiteSpace(surface) && !AdapterTier.KnownSurfaces.ContainsKey(surface))
        {
            errors.Add($"{label} has surface '{surface}', which is not in the closed set " +
                $"{{{string.Join(", ", AdapterTier.KnownSurfaces.Keys.Select(s => $"'{s}'"))}}} " +
                "that AdapterTierScanner walks");
        }

        if (!string.IsNullOrWhiteSpace(privilege) && !string.IsNullOrWhiteSpace(surface)
            && AdapterTier.KnownSurfaces.TryGetValue(surface, out var expectedPrivilege)
            && privilege != expectedPrivilege)
        {
            errors.Add($"{label} has privilege '{privilege}', but surface '{surface}' grants " +
                $"'{expectedPrivilege}' — a row's privilege must equal AdapterTier.KnownSurfaces['{surface}']");
        }

        if (!string.IsNullOrWhiteSpace(reviewBy) && !IssuePattern.IsMatch(reviewBy))
        {
            errors.Add($"{label} has reviewBy '{reviewBy}', which must match ^#[0-9]+$ — an exemption needs an end date");
        }
    }

    private static void ValidateCompatibilityException(CompatibilityException row, string label, List<string> errors)
    {
        var deleteWhen = row.DeleteWhen;
        Required(deleteWhen, "deleteWhen", label, errors);
        if (!string.IsNullOrWhiteSpace(deleteWhen) && !IssuePattern.IsMatch(deleteWhen))
        {
            errors.Add($"{label} has deleteWhen '{deleteWhen}', which must match ^#[0-9]+$ — " +
                "the trigger is the slice issue that deletes the exception, never a date");
        }

        if (NonBlank(row.Tables, "tables", label, errors).Count == 0)
            errors.Add($"{label} names no tables");
        Required(row.Symbol, "symbol", label, errors);
        Required(row.Reaches, "reaches", label, errors);
        Required(row.Owner, "owner", label, errors);
        Required(row.Reason, "reason", label, errors);
    }

    private static void ValidateOwner(OwnerDefinition owner, List<string> errors)
    {
        var (name, kind) = (owner.Name, owner.Kind);
        var label = $"owner '{name}'";
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("owner with a blank name");
        }

        if (kind is not (ModuleKind or PlatformKind))
        {
            errors.Add($"owner '{name}' has kind '{kind}' — must be '{ModuleKind}' or '{PlatformKind}'");
        }

        var namespaces = NonBlank(owner.Namespaces, "namespaces", label, errors);
        var exact = NonBlank(owner.ExactNamespaces, "exactNamespaces", label, errors);
        if (namespaces.Count == 0 && exact.Count == 0)
        {
            errors.Add($"owner '{name}' claims no namespaces");
        }

        var contract = NonBlank(owner.Contract, "contract", label, errors);
        if (contract.Count > 0 && kind == PlatformKind)
        {
            errors.Add($"owner '{name}' is a platform owner and cannot declare a contract");
        }

        var seam = NonBlank(owner.Seam, "seam", label, errors);
        if (seam.Count > 0 && contract.Count == 0)
        {
            errors.Add($"owner '{name}' declares a seam but no contract; only a contracted owner's types are checked, so the seam would excuse nothing");
        }
        foreach (var type in seam.Intersect(contract, StringComparer.Ordinal))
        {
            errors.Add($"owner '{name}' lists '{type}' in both its contract and its seam");
        }
    }

    private static string EdgeLabel(string from, string to, int index) =>
        string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) ? $"edges[{index}]" : $"edge {from} -> {to}";

    private static void ValidateEdge(EdgeCell edge, int index, List<string> errors)
    {
        var label = EdgeLabel(edge.From, edge.To, index);
        if (string.IsNullOrWhiteSpace(edge.From) || string.IsNullOrWhiteSpace(edge.To))
        {
            errors.Add($"edges[{index}] is missing 'from' or 'to'");
        }

        if (edge.Kind is not ("W" or "R"))
        {
            errors.Add($"{label} has kind '{edge.Kind}' — must be 'W' or 'R'");
        }

        if (string.IsNullOrWhiteSpace(edge.Reason))
        {
            errors.Add($"{label} has a blank reason — an undocumented cell is what this ledger exists to prevent");
        }

        NonBlank(edge.Symbols, "symbols", label, errors);
    }

    // Reports each blank entry and returns the rest, which the checks after it count.
    private static IReadOnlyList<string> NonBlank(
        IReadOnlyList<string> values, string name, string label, List<string> errors)
    {
        foreach (var _ in values.Where(string.IsNullOrWhiteSpace))
            errors.Add($"{label} has a blank or non-string entry in '{name}'");
        return values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
    }
}
