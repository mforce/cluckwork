using System.Text.Json;
using System.Text.RegularExpressions;

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

    // #850: types outside the module's namespaces trusted to read its tables.
    public IReadOnlyList<string> Implementations { get; init; } = [];

    // #1023: non-contract types peer modules may still reach. Adapters may not, and the contract walk skips
    // them, because a seam can carry an aggregate on purpose (#851's account seam).
    public IReadOnlyList<string> Seam { get; init; } = [];

    // #1023: types this owner claims inside a Platform namespace.
    public IReadOnlyList<string> Types { get; init; } = [];
}

public sealed record EdgeCell(string From, string To, string Kind, string Reason, IReadOnlyList<string> Symbols);

public sealed record TableClaim(string Owner, string Table);

public sealed record ForeignKeyCell(string Table, string Name, string From, string To, string Reason);

public sealed record TableOwnerOverride(string Table, string Reason);

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
    public IReadOnlyList<TableClaim> Tables { get; init; } = [];
    public IReadOnlyList<ForeignKeyCell> ForeignKeys { get; init; } = [];
    public IReadOnlyList<TableOwnerOverride> TableOwnerOverrides { get; init; } = [];

    public AdapterRoots AdapterRoots { get; init; } = new([], []);
    public IReadOnlyList<AdapterClaim> Adapters { get; init; } = [];
    public IReadOnlyList<AdapterTier> AdapterTiers { get; init; } = [];
    public IReadOnlyList<CompatibilityException> CompatibilityExceptions { get; init; } = [];

    public IEnumerable<string> AdapterNamespaces =>
        AdapterRoots.Namespaces.Concat(AdapterTiers.Select(t => t.Namespace));

    public IEnumerable<string> PersistenceForbiddenNamespaces =>
        AdapterRoots.PersistenceForbiddenNamespaces.Concat(AdapterTiers.Select(t => t.Namespace));

    public const string ModuleKind = "module";
    public const string PlatformKind = "platform";

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex IssuePattern = new("^#[0-9]+$", RegexOptions.Compiled);

    // Reads the JSON into records. Reports only what a record cannot express: a missing file, invalid JSON,
    // a section or row of the wrong JSON type, a non-string value. Every rule about the values is Validate's.
    public static ModuleLedger Parse(string path)
    {
        if (!File.Exists(path))
        {
            return new ModuleLedger([], [], [$"ledger file not found: {path}"]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            return new ModuleLedger([], [], [$"ledger is not valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            var errors = new List<string>();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ModuleLedger([], [], ["ledger root must be a JSON object with 'owners' and 'edges'"]);
            }

            foreach (var duplicate in root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
                errors.Add($"duplicate top-level section '{duplicate.Key}'");

            var owners = new List<OwnerDefinition>();
            if (!root.TryGetProperty("owners", out var ownersElement) || ownersElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("ledger has no 'owners' object");
            }
            else
            {
                foreach (var owner in ownersElement.EnumerateObject())
                {
                    owners.Add(ReadOwner(owner, errors));
                }
            }

            var edges = new List<EdgeCell>();
            if (!root.TryGetProperty("edges", out var edgesElement) || edgesElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("ledger has no 'edges' array");
            }
            else
            {
                var index = 0;
                foreach (var edge in edgesElement.EnumerateArray())
                {
                    edges.Add(ReadEdge(edge, index++, errors));
                }
            }

            var tables = new List<TableClaim>();
            if (root.TryGetProperty("tables", out var tablesElement))
            {
                if (tablesElement.ValueKind != JsonValueKind.Object)
                    errors.Add("ledger 'tables' must be an object");
                else
                {
                    // Validate reports an unknown owner once, at its first claim. JSON reports it per key, so
                    // a key that adds no claim, or repeats an owner, is reported here.
                    var reported = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var owner in tablesElement.EnumerateObject())
                    {
                        var claims = ReadArray(owner.Value, owner.Name, "tables", errors);
                        if ((claims.Count == 0 || !reported.Add(owner.Name)) && !owners.Any(o => o.Name == owner.Name))
                            errors.Add($"tables references unknown owner '{owner.Name}'");
                        tables.AddRange(claims.Select(table => new TableClaim(owner.Name, table)));
                    }
                }
            }

            var foreignKeys = ReadRows(root, "foreignKeys", errors, row =>
                new ForeignKeyCell(StringOrEmpty(row, "table"), StringOrEmpty(row, "name"),
                    StringOrEmpty(row, "from"), StringOrEmpty(row, "to"), StringOrEmpty(row, "reason")));
            var overrides = ReadRows(root, "tableOwnerOverrides", errors, row =>
                new TableOwnerOverride(StringOrEmpty(row, "table"), StringOrEmpty(row, "reason")));

            var adapterRoots = new AdapterRoots([], []);
            if (root.TryGetProperty("adapterRoots", out var roots))
            {
                if (roots.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("ledger 'adapterRoots' must be an object");
                }
                else
                {
                    adapterRoots = new AdapterRoots(
                        ReadStringArray(roots, "namespaces", "adapterRoots", errors),
                        ReadStringArray(roots, "types", "adapterRoots", errors))
                    {
                        TopLevelPrograms = roots.TryGetProperty("topLevelPrograms", out _)
                            ? ReadStringArray(roots, "topLevelPrograms", "adapterRoots", errors) : [],
                        PersistenceForbiddenNamespaces = ReadStringArray(
                            roots, "persistenceForbiddenNamespaces", "adapterRoots", errors),
                    };
                }
            }

            var adapters = ReadRows(root, "adapters", errors, (row, label) =>
                new AdapterClaim(StringOrEmpty(row, "symbol"),
                    row.ValueKind == JsonValueKind.Object
                        ? ReadStringArray(row, "reaches", label, errors) : []));

            var adapterTiers = ReadRows(root, "adapterTiers", errors, row =>
                new AdapterTier(StringOrEmpty(row, "namespace"), StringOrEmpty(row, "privilege"),
                    StringOrEmpty(row, "surface"), StringOrEmpty(row, "reason"), StringOrEmpty(row, "reviewBy")));

            var compatibilityExceptions = ReadRows(root, "compatibilityExceptions", errors, (row, label) =>
                new CompatibilityException(StringOrEmpty(row, "symbol"), StringOrEmpty(row, "reaches"),
                    row.ValueKind == JsonValueKind.Object ? ReadStringArray(row, "tables", label, errors) : [],
                    StringOrEmpty(row, "owner"), StringOrEmpty(row, "reason"), StringOrEmpty(row, "deleteWhen")));

            return new ModuleLedger(owners, edges, errors)
            {
                AdapterRoots = adapterRoots,
                Adapters = adapters,
                AdapterTiers = adapterTiers,
                CompatibilityExceptions = compatibilityExceptions,
                Tables = tables,
                ForeignKeys = foreignKeys,
                TableOwnerOverrides = overrides,
            };
        }
    }

    // Every rule about the values, over records however they were built. Returns the ledger with its
    // errors appended; the records are not changed.
    public static ModuleLedger Validate(ModuleLedger ledger)
    {
        var errors = new List<string>(ledger.RegistryErrors);

        foreach (var owner in ledger.Owners)
            ValidateOwner(owner, errors);

        for (var index = 0; index < ledger.Edges.Count; index++)
            ValidateEdge(ledger.Edges[index], index, errors);

        foreach (var claims in ledger.Tables.GroupBy(t => t.Owner, StringComparer.Ordinal))
        {
            if (!ledger.Owners.Any(o => o.Name == claims.Key))
                errors.Add($"tables references unknown owner '{claims.Key}'");
            NonBlank(claims.Select(c => c.Table).ToList(), claims.Key, "tables", errors);
        }

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

    private static IReadOnlyList<T> ReadRows<T>(JsonElement root, string name, List<string> errors,
        Func<JsonElement, T> read) => ReadRows(root, name, errors, (row, _) => read(row));

    private static IReadOnlyList<T> ReadRows<T>(JsonElement root, string name, List<string> errors,
        Func<JsonElement, string, T> read)
    {
        if (!root.TryGetProperty(name, out var array))
            return [];
        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"ledger '{name}' must be an array");
            return [];
        }

        var rows = new List<T>();
        foreach (var row in array.EnumerateArray())
        {
            var label = $"{name}[{rows.Count}]";
            if (row.ValueKind != JsonValueKind.Object)
                errors.Add($"{label} is not an object");
            rows.Add(read(row, label));
        }
        return rows;
    }

    // A missing or non-string value reads as empty, and Validate's blank check reports it.
    private static string StringOrEmpty(JsonElement row, string name) =>
        (row.ValueKind == JsonValueKind.Object ? ReadString(row, name) : null) ?? string.Empty;

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

    private static OwnerDefinition ReadOwner(JsonProperty property, List<string> errors)
    {
        var name = property.Name;
        if (property.Value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"owner '{name}' is not an object");
            return new OwnerDefinition(name, string.Empty, [], []);
        }

        var label = $"owner '{name}'";
        return new OwnerDefinition(name, ReadString(property.Value, "kind") ?? string.Empty,
            ReadStringArray(property.Value, "namespaces", label, errors),
            ReadOptionalStringArray(property.Value, "exactNamespaces", label, errors))
        {
            Contract = ReadOptionalStringArray(property.Value, "contract", label, errors),
            Implementations = ReadOptionalStringArray(property.Value, "implementations", label, errors),
            Seam = ReadOptionalStringArray(property.Value, "seam", label, errors),
            Types = ReadOptionalStringArray(property.Value, "types", label, errors),
        };
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

        NonBlank(owner.Implementations, "implementations", label, errors);

        var seam = NonBlank(owner.Seam, "seam", label, errors);
        if (seam.Count > 0 && contract.Count == 0)
        {
            errors.Add($"owner '{name}' declares a seam but no contract; only a contracted owner's types are checked, so the seam would excuse nothing");
        }
        foreach (var type in seam.Intersect(contract, StringComparer.Ordinal))
        {
            errors.Add($"owner '{name}' lists '{type}' in both its contract and its seam");
        }

        var types = NonBlank(owner.Types, "types", label, errors);
        if (types.Count > 0 && kind == PlatformKind)
        {
            errors.Add($"owner '{name}' is a platform owner and cannot claim types");
        }
    }

    private static EdgeCell ReadEdge(JsonElement element, int index, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"edges[{index}] is not an object");
            return new EdgeCell(string.Empty, string.Empty, string.Empty, string.Empty, []);
        }

        var from = ReadString(element, "from") ?? string.Empty;
        var to = ReadString(element, "to") ?? string.Empty;
        return new EdgeCell(from, to, ReadString(element, "kind") ?? string.Empty,
            ReadString(element, "reason") ?? string.Empty,
            ReadStringArray(element, "symbols", EdgeLabel(from, to, index), errors));
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

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadOptionalStringArray(
        JsonElement element, string name, string label, List<string> errors) =>
        element.TryGetProperty(name, out _) ? ReadStringArray(element, name, label, errors) : [];

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element, string name, string label, List<string> errors)
    {
        return ReadArray(element.TryGetProperty(name, out var array) ? array : default, name, label, errors);
    }

    // Drops and reports a blank or non-string entry, so Validate never sees a blank one from JSON.
    private static IReadOnlyList<string> ReadArray(
        JsonElement array, string name, string label, List<string> errors)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{label} has no '{name}' array");
            return [];
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{label} has a blank or non-string entry in '{name}'");
                continue;
            }

            values.Add(value);
        }

        return values;
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
