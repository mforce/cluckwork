# Put Egg Operations behind a contract and pin the egg lot lock order (#853)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md),
> beside the #849 contract rule; this file records what the Egg Operations
> contract covers, the lock order it pins, and what it leaves for Commerce (#854).

**Status:** accepted
**Date:** 2026-10-02

## What happened

No incident. This is epic #514, Track C slice 11, the fifth module behind a
contract after Finance (#849), Farm (#851), Flock Management (#852) and General
Inventory (#855). Egg Operations owns daily entries, egg grades, egg lots and the
egg ledger, the largest invariant cluster in the system.

Before this change, adapters reached Egg Operations through 37 parameter crossings
of 21 non-contract types across 24 adapter rows: the daily-entry, grade, stock and
sales endpoints and both seeders. The count comes from running the #849 adapter
check against `main` at 54a5f298 with a placeholder contract. After it, the count
is 0. The lock sweep adds a 25th row, so the matrix cell goes from `A (24)` to
`A (25)`.

The design checkpoint found that nothing tested the order in which lots are
locked. Reversing the `ORDER BY` in `GetByIdsLockedAsync` and
`GetByDailyEntryLockedAsync` left all 1881 integration tests green. This slice adds
`EggLotLockOrderTests` first; see below.

It also found that the issue body is wrong about audit. `SubmitDailyEntryHandler`
writes a `DailyEntry.Submit` row since #494, and
`AuditProvenanceTests.DailyEntrySubmit_WritesAnAuditEvent` checks it. The rule held
here is that every handler writes the audit rows it wrote before.

## The rule

The contract has three interfaces:

- `IEggOperationsModule` in `Cluckwork.Application.Features.Eggs` is the adapter
  surface. It records, submits, adjusts, voids, gets and lists daily entries; lists,
  gets, creates, updates, activates and deactivates grades; reads stock by grade,
  lots and a lot's movements; records a lot movement; and locks due entries for the
  sweep. Endpoints, both seeders and `DailyEntryLockSweep` call it.
- `IEggGradeLookup` is the grade read port. `SaleEndpoints` reads grade names
  through it.
- `IDailyEntryLookup` is the daily-entry read port. `RecordFeedUsageHandler` and
  `RecordWaterUsageHandler` read the day's entry id through it.

Reads return `DailyEntryDetails`, `EggGradeDetails`, `EggLotDetails`,
`EggLotMovementDetails` and the existing `StockByGrade`. Writes take the existing
commands and return the existing responses. `EggOperationsModule` only forwards to
the existing handlers and four repositories, and each lookup forwards to one
repository method, so the SQL and the filters do not change.
`owners.EggOperations.contract` lists 26 types: the three interfaces, eight
records, six commands and the grade-line DTO they carry, four responses, and the
four `Domain.Eggs` enums the records expose.
The four repositories are its `implementations` under #850.

The sweep's lock loop moved, unchanged, into `LockDueDailyEntriesHandler`. The sweep
still picks the account, the cutoff and the batch size, and it still logs each
locked entry after the save. One log line moved: the warning for an entry that
refuses to lock now prints after the save instead of before it. That branch cannot
run, because the query selects only Submitted entries and `Lock` refuses only
non-Submitted ones.

## Why three interfaces, not one

#852's rule applies: split when a peer module, not an adapter, needs a small part of
the module. Feed and water usage need one daily-entry read each, and Commerce needs
grade reads. Injecting `IEggOperationsModule` there would construct nine handlers
per request to use none of them. `SaleEndpoints` is an adapter, but it needs only
grade names, so it takes the lookup too. #854 adds the grade reads Commerce's
handlers need to `IEggGradeLookup`.

## The lock order

Every `FOR UPDATE` read over `EggLots` orders by `(ProductionDate, Id)`, so a
confirm, a sale void and a daily-entry void that touch the same lots queue rather
than deadlock. The three reads are `GetAvailableFifoLockedAsync` (confirm),
`GetByIdsLockedAsync` (sale void) and `GetByDailyEntryLockedAsync` (entry adjust and
void).

Two tests pin it.

`EggLotLockOrderTests` checks the order behaviourally. Each test holds the
canonically later lot in a second connection, waits until the request is parked
on it (`pg_blocking_pids`), then tries the earlier lot with `FOR UPDATE NOWAIT`.
The try must fail, because the request already holds that lot. The confirm and
sale-void tests run with lots on different dates and with lots on one date; a
daily entry's lots always share a date. The tests ask PostgreSQL, the database
that applies the lock order, which Id is lower, rather than relying on a C#
comparison. The seeds keep any other row order from matching the canonical one
by luck: the later lot is inserted first and holds fewer eggs, and on different
dates it has the smaller Id. Heap order, `IX_EggLots_Allocation`'s quantity
order and primary-key order then disagree with the canonical order wherever
they can.

Each of these changes turned its test red on all of three runs: a reversed
order, a reversed `Id` part, and a dropped `ORDER BY` on each of the three reads,
and a dropped `Id` tie-breaker on the confirm and daily-entry reads. One change stays invisible to it: a dropped `Id`
tie-breaker on the sale-void read. That read runs as a bitmap heap scan after the
confirm has rewritten both lots, in canonical order, so it already returns them
in that order and nothing observable changes.

`EggLotLockSqlTests` covers that gap and every other text change. It walks
`src/` with Roslyn for each SQL string that takes `FOR UPDATE` on `"EggLots"`,
requires `ORDER BY "ProductionDate", "Id"` directly before `FOR UPDATE`, and fails
below three statements. The tenant-bypass allow-list does not catch any of these
changes, because it keys raw-SQL reads by signature rather than by SQL text.

## The stock seam waits for Commerce

Commerce's handlers still call `IEggLotRepository`, `IEggInventoryMovementRepository`
and `IEggGradeRepository`. The owner moved the stock seam to #854, so this slice
changes no Commerce handler. The seam's shape comes from the confirm transaction,
which #854 owns. Any seam #854 builds must keep these properties of today's code:

- **One lock.** `ConfirmSaleHandler` takes one `FOR UPDATE` over every available lot
  of every grade on the order, farm-wide, ordered `(ProductionDate, Id)`. It does
  this after the account `FOR SHARE`, the order `FOR UPDATE`, the fresh role read
  and the discount-ceiling check, and never filters by flock in SQL (#612). The seam
  locks once and never twice.
- **Two plans over the same locked rows.** For a restricted Worker under
  `AssignedFlocksOnly`, the handler plans first over the locked lots whose `FlockId`
  is an assigned flock, then, only to pick the refusal, over every locked lot. There
  is no second query or lock. The seam therefore needs each locked lot's `FlockId`
  and available quantity, or must take the assigned-flock set and report complete,
  short in assigned flocks only, or short farm-wide with the grade and the shortfall.
- **Apply only the chosen plan.** `EggLot.Allocate(quantity, allocationDate)` runs on
  the same locked instances the plan read. A failure there is an invariant
  violation and throws; it is never a 422.
- **Sale movements reference the allocation.** Each draw writes a `Sale` movement of
  `-quantity` with reference type `SalesOrderAllocation` and Commerce's allocation
  id, so Commerce mints the allocation id before the movement. Two same-grade lines
  drawing from one lot stay distinguishable.
- **Void restores by lot.** `VoidSaleHandler` sums the pending allocations per lot,
  locks those lots by id in the same canonical order, refuses with
  `EggLot.AllocationSourceMissing` when one is gone, calls `EggLot.Restore` per lot,
  and writes a `Void` movement of `+quantity` with reference type `SalesOrder`, the
  order id and the void reason.
- **No save.** Everything runs inside Commerce's transaction. The seam adds to the
  caller's unit of work and never saves, like `IMortalityLedger` (#852).

`EggLotLockOrderTests`, `EggLotConcurrencyTests` and
`VoidSaleTests.VoidRacingConfirm_OnSameLots_NeverLosesOrDuplicatesStock` hold the
order and the outcomes while #854 moves those calls.

## What this does NOT cover

- Commerce's handlers, as above. The Commerce to Egg Operations `W` edge is
  unchanged.
- Peer modules were not checked here (#849, #852). Since #1023 a peer that injects
  `IDailyEntryRepository` again fails `PeerContractRealTreeTests`.
- `EggGradeFloorPolicy` stays inside the module: no adapter calls it. Its read of
  `Domain.Accounts.Roles` stays an Egg Operations to Farm `R` edge for #857.
- `AccountProvisioner` still inserts a new farm's default grades inside its own
  transaction, through the declared Access to Egg Operations `W` edge (#857).
- Seeder reads. Five seeder methods read Egg Operations tables to clean up a partial
  seed, check base data, count the manifest and converge on a re-run. They are
  compatibility exceptions that #858 deletes; the contract does not grow reads only
  a seeder needs (#850, #855). The simulation seeder's natural-key check became the
  identical `db.DailyEntries.AnyAsync`, without tracking the entry.
- `BirdMovement.DailyEntryId` stays a schema-only foreign key from Flock Management
  (#852).
- Method bodies. `IEggOperationsModule.DailyEntryAuditEntityType` and
  `EggGradeAuditEntityType` exist for the endpoints' provenance reads.

## How it is enforced

- `AdapterReachRealTreeTests` reports a contract bypass for any adapter that takes a
  non-contract Egg Operations type. Adding `IEggLotRepository eggLots` back to
  `StockEndpoints.ListLots` turns it red, and so does deleting the sweep's adapter
  row.
- `ModuleContractRealAssemblyTests` walks the contract types. Adding a member that
  returns `EggLot` to `IEggOperationsModule` turns it red.
- `CompatibilityExceptionRealTreeTests` fails on an unregistered read of an Egg
  Operations table. Removing `EggLotRepository` from `implementations`, or the
  seeder's natural-key row, turns it red.
- `EggOperationsModuleTests` pins the copies and the forwarded arguments. Swapping
  `FarmId` and `HouseId`, `CrackedGradeId` and `DirtyGradeId`, or
  `QuantityProduced` and `QuantityAvailable` turns it red. So do dropping the lot
  check before a movement read, dropping the farm from the active-grade list, and
  swapping the lookup's natural key.
- `EggLotLockOrderTests` and `EggLotLockSqlTests` pin the lock order, as above.
