# Business dates, row timestamps, and list sequences are separate facts (#819)

## What happened

Several lists ordered rows by a farm business date and then by a random UUID.
Rows from the same day therefore had a stable but arbitrary order. A recent row
could appear below older rows or beyond the first page. The first change added a
database identity sequence to five lists. A full model walk found six more
chronological lists and showed that most business tables also lacked consistent
creation and update metadata.

## The rule

Farm business dates remain `DateOnly`. They describe the day on which farm work
belongs. `CreatedAtUtc` and `UpdatedAtUtc` describe database writes. Every mapped
business record except `AuditEvent` implements `ICreatedRecord`. A record that
supports an in-place update implements `IMutableRecord`. PostgreSQL stamps both
fields with `timestamp with time zone` values. On insert, mutable rows receive
the same value for both fields. On update, PostgreSQL preserves `CreatedAtUtc`
and replaces `UpdatedAtUtc`.

The business-record census includes the 25 domain record types other than
`AuditEvent`, plus `ApplicationUser`. `AuditEvent` keeps its established
`OccurredAtUtc` meaning. ASP.NET Identity support types, including passkey data,
and the operational refresh-token, idempotency, durable-job, and simulation-seed
tables retain their own lifecycle fields and do not join this convention.

Twelve chronological tables also carry a shadow `bigint GENERATED ALWAYS AS
IDENTITY` property named `Sequence`: `SalesOrders`, `SalesOrderItems`, `Expenses`, `DailyEntries`,
`EggLots`, `BirdMovements`, `Payments`, `InventoryLots`, `FeedUsages`,
`WaterUsages`, `InventoryMovements`, and `EggInventoryMovements`. Each table has
a unique constraint on `Sequence`. Interactive lists with a business date order
by business date, creation time, and sequence, all descending.
`EggInventoryMovements` has no independent business date and orders by creation
time and sequence. The sequence stays outside domain objects, API responses,
exports, and screens.

The generic timestamps are persistence metadata in this change. Existing API
and CSV fields stay compatible; a later contract change can expose the new
timestamps where a user needs them. Datasets that already exported a creation
timestamp continue to do so.

Exports keep their established direction but use the same complete tuple in
that direction. FIFO selection and `FOR UPDATE` lock acquisition keep their
existing business-date and UUID order because those queries define allocation
behavior and canonical lock order rather than presentation chronology.
The flock export also keeps its established `PlacementDate, Id` order: flock
screens are name-ordered, so flocks are not a chronological paged list and do
not gain a persistence sequence solely for export formatting.

## Existing rows

The migration preserves every stored `CreatedAtUtc`. For a table that did not
store it, the migration uses the earliest exact creation audit whose
`EntityType`, `EntityId`, and action match a frozen table-specific whitelist.
If no such event exists, it uses `1970-01-01T00:00:00Z` as an explicit unknown
sentinel. The sentinel is not a recovered historical time.
Fresh databases replay the same migration history, so base reference rows that
predate this migration also receive the sentinel; it means unknown there too,
not that those records were created in 1970.

For mutable rows, the migration uses the latest exact mutating audit from a
frozen table-specific whitelist. It ignores read-only actions and events aimed
at a parent record. If no qualifying update exists, `UpdatedAtUtc` equals
`CreatedAtUtc`. The migration never infers a write time from a business date or
a UUID.

Legacy sequence values provide deterministic order only. They cannot recover
the original insertion order. For new rows, PostgreSQL allocates `Sequence` at
insert time, not commit time. A transaction that commits later may have a lower
sequence than a transaction that commits earlier. Neither a timestamp nor an
identity sequence claims exact commit chronology.

`CreatedAtUtc` deliberately precedes `Sequence` in the agreed display tuple.
The sequence therefore resolves equal timestamps; it does not repair a system
clock that moves backwards. Offset pagination has a complete order for a fixed
dataset, but concurrent inserts can still move rows between pages. Cursor or
snapshot pagination would be a separate contract.

## Why PostgreSQL owns the timestamps

EF interceptors do not see `ExecuteUpdateAsync` or raw SQL. Cluckwork uses both,
and an owned-value update can also change a database row without presenting the
same tracked shape as an ordinary aggregate edit. PostgreSQL triggers cover all
of these write paths. Read-only interfaces expose the metadata without giving
handlers or factories another stamping path.

Action-specific fields remain separate. `OccurredAtUtc`, `LockedAtUtc`,
`ReleasedOnUtc`, `DisabledAt`, `FarmLogo.UpdatedAt`, and
`FarmLogo.BannerUpdatedAt` keep their existing meanings. Generic timestamps do
not replace audit history, domain event times, or optimistic `Version` tokens.

## Migration and enforcement

The unmerged sequence migration is replaced by one chronology migration. The
migration adds and backfills columns before it installs triggers. It takes table
locks and runs through the pre-deploy migration process. Schema documentation
is regenerated from the final model.

The migration begins with a five-second `lock_timeout`. That limits how long it
waits to acquire a lock, not how long acquired locks remain held; the migration
transaction retains them until commit. CI proves the upgrade on its fixture,
not its duration at production volume, so deployment must time it on a
production-sized copy before running the pre-deploy job.

An executable model census classifies every mapped type as mutable,
create-only, or excluded. It also fixes the set of chronological tables. A new
mapped type cannot rely on a developer remembering this document alone.

## Sales line display order (#906)

Sales order list and detail reads load lines by `CreatedAtUtc`, then the shadow
`Sequence`, ascending. SalesPage renders that array as received; the Dashboard
uses its first line. The aggregate appends new lines, and timestamp ties now
retain database insertion order rather than an arbitrary UUID order. No position
field existed previously. The sequence migration gives existing rows a stable
tie-breaker but cannot recover the original insertion order of legacy ties.
The sequence is not returned by the API. Allocation lock ordering is unchanged.
