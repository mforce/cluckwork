namespace Cluckwork.Application.Tests.Architecture;

// #842 — the committed module ledger: owners by namespace, one cell per cross-owner dependency.

using System.Text.Json;

public sealed record OwnerDefinition(
    string Name,
    string Kind,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string> ExactNamespaces);

public sealed record EdgeCell(string From, string To, string Kind, string Reason, IReadOnlyList<string> Symbols);

public sealed record TableClaim(string Owner, string Table);

public sealed record ForeignKeyCell(string Table, string Name, string From, string To, string Reason);

public sealed record TableOwnerOverride(string Table, string Reason);

public sealed record AdapterRoots(IReadOnlyList<string> Namespaces, IReadOnlyList<string> Types)
{
    public IReadOnlyList<string> PersistenceForbiddenNamespaces { get; init; } = [];
}

public sealed record AdapterClaim(string Symbol, IReadOnlyList<string> Reaches);

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

    public const string ModuleKind = "module";
    public const string PlatformKind = "platform";

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModuleLedger Load(string path)
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
                    foreach (var owner in tablesElement.EnumerateObject())
                    {
                        if (!owners.Any(o => o.Name == owner.Name))
                            errors.Add($"tables references unknown owner '{owner.Name}'");
                        foreach (var table in ReadArray(owner.Value, owner.Name, "tables", errors))
                            tables.Add(new TableClaim(owner.Name, table));
                    }
            }

            var foreignKeys = ReadRows(root, "foreignKeys", errors, (row, label) =>
                new ForeignKeyCell(RequiredString(row, "table", label, errors), RequiredString(row, "name", label, errors),
                    RequiredString(row, "from", label, errors), RequiredString(row, "to", label, errors),
                    RequiredString(row, "reason", label, errors)));
            var overrides = ReadRows(root, "tableOwnerOverrides", errors, (row, label) =>
                new TableOwnerOverride(RequiredString(row, "table", label, errors),
                    RequiredString(row, "reason", label, errors)));

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
                        PersistenceForbiddenNamespaces = ReadStringArray(
                            roots, "persistenceForbiddenNamespaces", "adapterRoots", errors),
                    };
                }
            }

            var adapters = ReadRows(root, "adapters", errors, (row, label) =>
                new AdapterClaim(RequiredString(row, "symbol", label, errors),
                    row.ValueKind == JsonValueKind.Object
                        ? ReadStringArray(row, "reaches", label, errors) : []));

            return new ModuleLedger(owners, edges, errors)
            {
                AdapterRoots = adapterRoots,
                Adapters = adapters,
                Tables = tables,
                ForeignKeys = foreignKeys,
                TableOwnerOverrides = overrides,
            };
        }
    }

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

    private static string RequiredString(JsonElement row, string name, string label, List<string> errors)
    {
        var value = row.ValueKind == JsonValueKind.Object ? ReadString(row, name) : null;
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{label} has a blank or non-string '{name}'");
        return value ?? string.Empty;
    }

    private static OwnerDefinition ReadOwner(JsonProperty property, List<string> errors)
    {
        var name = property.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("owner with a blank name");
        }

        if (property.Value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"owner '{name}' is not an object");
            return new OwnerDefinition(name, string.Empty, [], []);
        }

        var kind = ReadString(property.Value, "kind");
        if (kind is not (ModuleKind or PlatformKind))
        {
            errors.Add($"owner '{name}' has kind '{kind ?? "<missing>"}' — must be '{ModuleKind}' or '{PlatformKind}'");
        }

        var namespaces = ReadStringArray(property.Value, "namespaces", $"owner '{name}'", errors);
        var exact = property.Value.TryGetProperty("exactNamespaces", out _)
            ? ReadStringArray(property.Value, "exactNamespaces", $"owner '{name}'", errors)
            : [];
        if (namespaces.Count == 0 && exact.Count == 0)
        {
            errors.Add($"owner '{name}' claims no namespaces");
        }

        return new OwnerDefinition(name, kind ?? string.Empty, namespaces, exact);
    }

    private static EdgeCell ReadEdge(JsonElement element, int index, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"edges[{index}] is not an object");
            return new EdgeCell(string.Empty, string.Empty, string.Empty, string.Empty, []);
        }

        var from = ReadString(element, "from");
        var to = ReadString(element, "to");
        var kind = ReadString(element, "kind");
        var reason = ReadString(element, "reason");
        var label = string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)
            ? $"edges[{index}]"
            : $"edge {from} -> {to}";

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            errors.Add($"edges[{index}] is missing 'from' or 'to'");
        }

        if (kind is not ("W" or "R"))
        {
            errors.Add($"{label} has kind '{kind ?? "<missing>"}' — must be 'W' or 'R'");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            errors.Add($"{label} has a blank reason — an undocumented cell is what this ledger exists to prevent");
        }

        var symbols = ReadStringArray(element, "symbols", label, errors);

        return new EdgeCell(from ?? string.Empty, to ?? string.Empty, kind ?? string.Empty, reason ?? string.Empty, symbols);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element, string name, string label, List<string> errors)
    {
        return ReadArray(element.TryGetProperty(name, out var array) ? array : default, name, label, errors);
    }

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
}
