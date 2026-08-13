# `AuditEvents` is not time-partitioned (#505)

Parked from the #494 review, so the reasoning is not rediscovered.

## Question

`AuditEvents` is append-only with no purge sweep, so it only grows. #494
added ~1 row per daily entry per day (`DailyEntry.Create` + `DailyEntry.Submit`),
on top of every other audited mutation. Should we get ahead of it by
range-partitioning the table by month?

## Answer: no, and not merely "not yet"

Time partitioning is the wrong axis for this table. Partition pruning only
helps a query that filters on the partition key. The dominant read —
`AuditEventRepository.GetProvenanceAsync`, run on every Flocks / Egg grades /
Daily entries / Sales / Expenses page load — filters on `AccountId` +
`EntityType` + `EntityId` and carries **no date predicate at all**.
Partitioning by month converts one index lookup on
`IX_AuditEvents_AccountId_EntityId` into one lookup *per partition*, getting
worse every month. It would degrade the exact query #494 introduced.

Three further costs, none of them the deciding one:

1. Postgres requires the partition key in every unique constraint, so the PK
   moves from `Id` to `(Id, OccurredAtUtc)`. Converting an existing table
   means create-new + copy + rename, not an `ALTER`.
2. Partition maintenance becomes a new failure mode with teeth. If next
   month's partition does not exist, `INSERT` fails; audit writes share the
   transaction with the change they record (#93), so *every audited
   mutation* fails with it. No partition-maintenance job runs today.
3. It drags in a migration (#407), a `docs/schema/` regeneration (#417), and
   a sim-harness pass (#370) — for a table that does not need any of them yet.

## Current size, for the record

~2 rows per flock per day. 10 flocks → ~7.3k rows/year; 100 flocks → ~73k
rows/year, roughly 20 MB/year. Not a problem at any plausible farm size.

## If it ever does bite, in order

1. **Archive or roll up** rows older than the retention the farm actually
   needs — but not naively. `GetProvenanceAsync` derives `CreatedBy*` from
   the record's original `*.Create` event and `MadeOfficialAtUtc` from its
   promotion event (submit/confirm), for as long as the record itself stays
   visible — which for a flock or a sales order can be indefinitely. Moving
   or aggregating those specific rows out of `AuditEvents` while the record
   they describe is still shown would silently blank its History column.
   Any archival design must keep provenance-source events queryable (or
   copy their fields forward) for every record still visible, not just
   drop/roll up by age.
2. **Partition by `AccountId`** — the column the provenance query *does*
   filter on, so pruning would apply. Only meaningful once the deployment is
   genuinely multi-tenant.

## Separate, and real

The Audit page pages with `OFFSET`, which degrades on deep pages as the
table grows. Independent of partitioning and of #494; worth keyset
pagination on its own merits if it ever shows up in practice — no issue
filed for it yet.
