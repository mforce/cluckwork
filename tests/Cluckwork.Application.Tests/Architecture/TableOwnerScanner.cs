using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cluckwork.Application.Tests.Architecture;

public sealed record CrossOwnerForeignKey(string Table, string Name, string From, string To);

public sealed record TableOwnerReport(
    int WalkedTableCount,
    IReadOnlyList<CrossOwnerForeignKey> CrossOwnerForeignKeys,
    IReadOnlyList<string> RegistryErrors,
    IReadOnlyList<string> Violations)
{
    public int ExpectedTableCountFloor { get; init; } = 30;

    // Table -> owner: the override's owner, else the CLR namespace's. A table with no single owner is absent.
    public IReadOnlyDictionary<string, string> Owners { get; init; } = new Dictionary<string, string>();
}

public static class TableOwnerScanner
{
    public static TableOwnerReport Scan(IModel model, ModuleLedger ledger)
    {
        var errors = new List<string>(ledger.RegistryErrors);
        var violations = new List<string>();
        var kinds = ledger.Owners.GroupBy(o => o.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        // Every table an entity maps to: its primary table plus any SplitToTable fragment.
        var mapped = model.GetEntityTypes()
            .SelectMany(e => TableStoreObjects(e).Select(t => (Table: Qualify(t.Name, t.Schema), Entity: e)))
            .GroupBy(p => p.Table, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var tableNames = mapped.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        ValidateRegistry(ledger, kinds, tableNames, errors);

        var overrides = ledger.TableOwnerOverrides.GroupBy(o => o.Table, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Owner, StringComparer.Ordinal);
        var tableOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in mapped)
        {
            // Shared owned values and join dictionaries do not reclassify their enclosing table.
            // Keep the group itself, including an owned-only or shadow-only distinct table.
            var entities = table.Select(p => p.Entity).Where(e => !e.IsOwned() && !e.HasSharedClrType).ToList();
            if (entities.Count == 0)
                entities = table.Select(p => p.Entity).ToList();
            var resolved = entities.Select(e => (Entity: e, Owners: ResolveOwners(e.ClrType.Namespace, ledger))).ToList();
            var namespaceOwners = resolved.SelectMany(r => r.Owners).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var unowned = resolved.FirstOrDefault(r => r.Owners.Length == 0).Entity;
            if (overrides.TryGetValue(table.Key, out var overrideOwner))
            {
                if (unowned is null && namespaceOwners is [var only] && only == overrideOwner)
                    violations.Add($"table-owner override '{table.Key}' restates its CLR namespace owner {only}");
                tableOwners[table.Key] = overrideOwner;
            }
            else if (unowned is not null)
                violations.Add($"table '{table.Key}' (entity {unowned.Name}) has no owner");
            else if (namespaceOwners.Length > 1)
                violations.Add($"table '{table.Key}' claimed by " +
                    string.Join(", ", resolved.Select(r => $"{string.Join(", ", r.Owners)} (entity {r.Entity.Name})")));
            else
                tableOwners[table.Key] = namespaceOwners[0];
        }

        string? Owner(string? table) => table is not null && tableOwners.TryGetValue(table, out var owner)
            && kinds.ContainsKey(owner) ? owner : null;

        var foreignKeys = new List<CrossOwnerForeignKey>();
        foreach (var entity in model.GetEntityTypes())
        {
            foreach (var fk in entity.GetForeignKeys())
            {
                foreach (var dependent in TableStoreObjects(entity))
                {
                    foreach (var principal in TableStoreObjects(fk.PrincipalEntityType))
                    {
                        var table = Qualify(dependent.Name, dependent.Schema);
                        var from = Owner(table);
                        var to = Owner(Qualify(principal.Name, principal.Schema));
                        var name = fk.GetConstraintName(dependent, principal);
                        if (name is null || from is null || to is null || from == to
                            || kinds[from] == ModuleLedger.PlatformKind || kinds[to] == ModuleLedger.PlatformKind)
                            continue;
                        foreignKeys.Add(new CrossOwnerForeignKey(table, name, from, to));
                    }
                }
            }
        }
        // Keyed by dependent table AND constraint name: PostgreSQL allows one
        // constraint name on several tables, and one row must excuse only one FK.
        var live = foreignKeys.Distinct().OrderBy(f => f.Table, StringComparer.Ordinal)
            .ThenBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.From, StringComparer.Ordinal)
            .ThenBy(f => f.To, StringComparer.Ordinal).ToList();
        foreach (var fk in live)
        {
            if (!ledger.ForeignKeys.Any(row => row.Table == fk.Table && row.Name == fk.Name && row.From == fk.From && row.To == fk.To))
                violations.Add($"undeclared cross-owner foreign key {fk.Name} on {fk.Table} from {fk.From} to {fk.To} — add to " +
                    $"RealModuleLedger.ForeignKeys: new({RealModuleLedger.Quote(fk.Table)}, {RealModuleLedger.Quote(fk.Name)}, " +
                    $"{RealModuleLedger.Quote(fk.From)}, {RealModuleLedger.Quote(fk.To)}, \"\"),");
        }
        foreach (var row in ledger.ForeignKeys)
        {
            var matches = live.Where(f => f.Table == row.Table && f.Name == row.Name).ToList();
            if (matches.Count == 0)
                violations.Add($"stale foreign-key row '{row.Name}' on {row.Table} from {row.From} to {row.To}");
            else if (!matches.Any(f => f.From == row.From && f.To == row.To))
                violations.Add($"foreign-key row '{row.Name}' on {row.Table} from {row.From} to {row.To} disagrees with model owners " +
                    string.Join(", ", matches.Select(f => $"{f.From} to {f.To}")));
        }

        return new TableOwnerReport(mapped.Count, live, errors, violations) { Owners = tableOwners };
    }

    public static IReadOnlyList<string> Evaluate(TableOwnerReport report)
    {
        var failures = report.RegistryErrors.Select(e => $"table-owner registry error: {e}").ToList();
        if (report.WalkedTableCount < report.ExpectedTableCountFloor)
            failures.Add($"walked {report.WalkedTableCount} tables, expected at least {report.ExpectedTableCountFloor}");
        failures.AddRange(report.Violations);
        return failures;
    }

    internal static IEnumerable<StoreObjectIdentifier> TableStoreObjects(IEntityType entity)
    {
        if (entity.GetTableName() is { } primary)
            yield return StoreObjectIdentifier.Table(primary, entity.GetSchema());

        foreach (var fragment in entity.GetMappingFragments()
                     .Where(f => f.StoreObject.StoreObjectType == StoreObjectType.Table))
            yield return fragment.StoreObject;
    }

    internal static string Qualify(string table, string? schema) =>
        schema is { } s && s != "public" ? $"{s}.{table}" : table;

    private static string[] ResolveOwners(string? ns, ModuleLedger ledger)
    {
        var claims = ledger.Owners.SelectMany(o => o.Namespaces.Concat(o.ExactNamespaces)
                .Select(n => (Owner: o.Name, Namespace: n)))
            .Where(c => ns == c.Namespace || (ns?.StartsWith(c.Namespace + ".", StringComparison.Ordinal) ?? false))
            .OrderByDescending(c => c.Namespace.Length).ToList();
        return claims.Count == 0 ? [] : claims.Where(c => c.Namespace.Length == claims[0].Namespace.Length)
            .Select(c => c.Owner).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateRegistry(ModuleLedger ledger, IReadOnlyDictionary<string, string> kinds,
        HashSet<string> tableNames, List<string> errors)
    {
        foreach (var duplicate in ledger.Owners.GroupBy(o => o.Name).Where(g => g.Count() > 1))
            errors.Add($"duplicate owner '{duplicate.Key}'");
        foreach (var row in ledger.ForeignKeys)
        {
            if (string.IsNullOrWhiteSpace(row.Table) || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Reason))
                errors.Add($"foreign-key row '{row.Name}' has a blank table, name or reason");
            foreach (var owner in new[] { row.From, row.To })
                if (!kinds.ContainsKey(owner))
                    errors.Add($"foreign-key row '{row.Name}' references unknown owner '{owner}'");
        }
        foreach (var duplicate in ledger.ForeignKeys.GroupBy(f => (f.Table, f.Name, f.From, f.To)).Where(g => g.Count() > 1))
            errors.Add($"duplicate foreign-key row '{duplicate.Key.Name}'");
        foreach (var row in ledger.TableOwnerOverrides)
        {
            if (string.IsNullOrWhiteSpace(row.Reason))
                errors.Add($"table-owner override '{row.Table}' has a blank reason");
            if (!tableNames.Contains(row.Table))
                errors.Add($"stale table-owner override '{row.Table}'");
            if (!kinds.ContainsKey(row.Owner))
                errors.Add($"table-owner override '{row.Table}' references unknown owner '{row.Owner}'");
        }
        foreach (var duplicate in ledger.TableOwnerOverrides.GroupBy(o => o.Table).Where(g => g.Count() > 1))
            errors.Add($"duplicate table-owner override '{duplicate.Key}'");
    }
}
