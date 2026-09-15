namespace Cluckwork.Application.Tests.Architecture;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

public sealed record CrossOwnerForeignKey(string Table, string Name, string From, string To);

public sealed record TableOwnerReport(
    int WalkedTableCount,
    IReadOnlyList<CrossOwnerForeignKey> CrossOwnerForeignKeys,
    IReadOnlyList<string> RegistryErrors,
    IReadOnlyList<string> Violations)
{
    public int ExpectedTableCountFloor { get; init; } = 30;
}

public static class TableOwnerScanner
{
    public static TableOwnerReport Scan(IModel model, ModuleLedger ledger)
    {
        var errors = new List<string>(ledger.RegistryErrors);
        var violations = new List<string>();
        var kinds = ledger.Owners.GroupBy(o => o.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        var claims = ledger.Tables.GroupBy(t => t.Table, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Owner).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        // Every table an entity maps to: its primary table plus any SplitToTable fragment.
        var mapped = model.GetEntityTypes()
            .SelectMany(e => TableStoreObjects(e).Select(t => (Table: Qualify(t.Name, t.Schema), Entity: e)))
            .GroupBy(p => p.Table, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var tableNames = mapped.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        ValidateRegistry(ledger, kinds, tableNames, errors);

        string? Owner(string? table) => table is not null && claims.TryGetValue(table, out var owners)
            && owners.Length == 1 && kinds.ContainsKey(owners[0]) ? owners[0] : null;

        var overridden = ledger.TableOwnerOverrides.Select(o => o.Table).ToHashSet(StringComparer.Ordinal);
        foreach (var table in mapped)
        {
            // Shared owned values and join dictionaries do not reclassify their enclosing table.
            // Keep the group itself, including an owned-only or shadow-only distinct table.
            var entities = table.Select(p => p.Entity).Where(e => !e.IsOwned() && !e.HasSharedClrType).ToList();
            if (entities.Count == 0)
                entities = table.Select(p => p.Entity).ToList();
            if (!claims.TryGetValue(table.Key, out var owners))
                violations.Add($"table '{table.Key}' (entity {entities[0].Name}) has no owner");
            else if (owners.Length > 1)
                violations.Add($"table '{table.Key}' claimed by {string.Join(", ", owners)}");
            else if (!overridden.Contains(table.Key))
            {
                foreach (var entity in entities)
                {
                    var namespaceOwners = ResolveOwners(entity.ClrType.Namespace, ledger);
                    if (namespaceOwners.Length != 1 || namespaceOwners[0] != owners[0])
                        violations.Add($"table '{table.Key}' owner {owners[0]} disagrees with CLR namespace owner " +
                            $"{(namespaceOwners.Length == 0 ? "<unowned>" : string.Join(", ", namespaceOwners))} " +
                            $"(entity {entity.Name})");
                }
            }
        }

        foreach (var row in ledger.Tables.Where(t => !tableNames.Contains(t.Table)))
            violations.Add($"stale table row '{row.Table}' under owner {row.Owner}");

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
                violations.Add($"undeclared cross-owner foreign key {fk.Name} on {fk.Table} from {fk.From} to {fk.To} — add " +
                    JsonSerializer.Serialize(new { table = fk.Table, name = fk.Name, from = fk.From, to = fk.To, reason = "" }));
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

        return new TableOwnerReport(mapped.Count, live, errors, violations);
    }

    public static IReadOnlyList<string> Evaluate(TableOwnerReport report)
    {
        var failures = report.RegistryErrors.Select(e => $"table-owner registry error: {e}").ToList();
        if (report.WalkedTableCount < report.ExpectedTableCountFloor)
            failures.Add($"walked {report.WalkedTableCount} tables, expected at least {report.ExpectedTableCountFloor}");
        failures.AddRange(report.Violations);
        return failures;
    }

    private static IEnumerable<StoreObjectIdentifier> TableStoreObjects(IEntityType entity)
    {
        if (entity.GetTableName() is { } primary)
            yield return StoreObjectIdentifier.Table(primary, entity.GetSchema());

        foreach (var fragment in entity.GetMappingFragments()
                     .Where(f => f.StoreObject.StoreObjectType == StoreObjectType.Table))
            yield return fragment.StoreObject;
    }

    private static string Qualify(string table, string? schema) =>
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
        foreach (var row in ledger.Tables)
        {
            if (!kinds.ContainsKey(row.Owner))
                errors.Add($"table '{row.Table}' references unknown owner '{row.Owner}'");
            if (string.IsNullOrWhiteSpace(row.Table))
                errors.Add($"blank table under owner {row.Owner}");
        }
        foreach (var duplicate in ledger.Tables.GroupBy(t => (t.Owner, t.Table)).Where(g => g.Count() > 1))
            errors.Add($"table '{duplicate.Key.Table}' listed twice under owner {duplicate.Key.Owner}");
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
        }
        foreach (var duplicate in ledger.TableOwnerOverrides.GroupBy(o => o.Table).Where(g => g.Count() > 1))
            errors.Add($"duplicate table-owner override '{duplicate.Key}'");
    }
}
