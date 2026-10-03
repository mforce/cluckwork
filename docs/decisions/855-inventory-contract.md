# Put General Inventory behind a contract and keep the feed-usage lock order (#855)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md),
> beside the #849 contract rule; this file records what the General Inventory
> contract covers, the order it must keep, and the exception it leaves for #854.

**Status:** accepted
**Date:** 2026-10-02

## What happened

No incident in this slice. This is epic #514, Track C slice 13, the fourth module
behind a contract after Finance (#849), Farm (#851) and Flock Management (#852).

Before this change, adapters reached General Inventory through 33 parameter
crossings of 22 non-contract types across 18 adapter rows: the inventory and
water endpoints and the simulation seeder. The count comes from running the #849
adapter check against `main` at 6e3f5845 with a placeholder contract. After it,
the count is 0. The 18 adapter rows still reach General Inventory, now through
its contract, so the matrix cell stays `A (18)`.

The design checkpoint for this slice found a defect in the order the slice had to
keep. #1022 fixed it first; see below.

## The rule

`IInventoryModule` in `Cluckwork.Application.Features.Inventory` is the contract.
It carries the item catalog (list, get, create, update, activate and deactivate),
purchases, lots, the movement ledger, adjustments, feed usage and water usage.
Reads return `InventoryItemDetails` (with the item's on-hand quantity),
`InventoryLotDetails`, `InventoryMovementDetails`, `FeedUsageDetails` and
`WaterUsageDetails`. Writes take the existing commands and return `Result`,
`Result<Guid>` or the existing `RecordFeedUsageResponse`. `InventoryModule` only
forwards to the eight existing handlers and five repositories.

It is one interface, not split like #852. #852 split Flock Management because peer
modules need a read port and an in-transaction write port. Only adapters call
General Inventory, and its one incoming edge, Insights' `ExportQueries`, reads
its tables through that declared edge.

The ledger lists the 14 contract types, and the five repositories as
`implementations` under #850.

## The feed-usage order

`RecordFeedUsageHandler` locks the inventory item, then reads flock eligibility
through `IFlockLookup`, then locks the FIFO lots, all in one transaction. The flock
row stays unlocked: a deplete or archive racing a feed is accepted.
`InventoryModule` calls the handler unchanged, so the contract keeps that order.

`FeedUsageLockOrderTests` pins both steps against a real database. Each test holds
one row lock, parks the usage request on it, archives the flock, then releases
the lock:

- Holding the item lock, the usage is refused, because eligibility is read after
  the item lock. Moving the eligibility read above the item lock turns it red.
- Holding the lot lock, the usage is recorded, because eligibility was already
  read before the lots. Moving the lot read above eligibility turns it red.

The first test failed on `main` before #1022. The handler reads the flock once
before the transaction and once after the item lock. Both reads were tracked, so
EF returned the first read's instance to the second, and the second read never saw
an archive that committed while the request waited. #1022 made `IFlockLookup`'s
reads untracked.

## The currency probe waits for Commerce

`CurrencyBoundRowProbe.AnyAsync` answers Farm's currency rule: a farm may change
currency only while nothing has written down an amount in the current one. It
reads Commerce (`SalesOrders`, `Payments`, `Products`), Finance (`Expenses`) and
General Inventory (`InventoryLots`, `FeedUsages`, `InventoryItems`).

#850 gave this slice the probe, because it expected General Inventory to be the
last of the three modules to get a contract. Commerce (#854) is now the last. Its
reads only become exceptions once Commerce declares a contract, so the probe
cannot move behind contracts before then.

A partial move was possible: one `bool` read on each of `IFinanceModule` and
`IInventoryModule`, with the probe keeping its Commerce reads. The owner chose
not to. It would widen Finance's contract and its repository port for one caller,
and #854 would reshape the probe again. Instead, the probe's General Inventory
read is a new compatibility exception (owner Farm), and both probe rows wait
for #854.

## What this does NOT cover

- `SimulationDataSeeder`'s own Inventory reads. Seven seeder methods read Inventory
  tables to converge on a re-run and to count the manifest. They are compatibility
  exceptions that #858 deletes, the same choice #850 made for Finance's seeder
  reads: the contract does not grow reads only the seeder needs. The seeder's one
  `HasLotsAsync` call became the identical `db.InventoryLots.AnyAsync` inside a
  method that already needed a row.
- The handlers' peer calls. Item create, item update and purchase keep Farm's
  `IAccountRepository` for the #162 `FOR SHARE` currency snapshot (#851). Feed
  usage, water usage and the water correction keep `IFlockLookup`, and the two
  record handlers read daily-entry provenance through Egg Operations'
  `IDailyEntryLookup` port since #853.
- Method bodies. Endpoints still name `InventoryItem.*` error codes in strings.
- Peer modules were not checked here (#849, #852). Since #1023
  `PeerContractRealTreeTests` checks them.

## How it is enforced

- `AdapterReachRealTreeTests` reports a contract bypass for any adapter that takes
  a non-contract General Inventory type. Adding `IInventoryLotRepository lots`
  back to `InventoryEndpoints.ListLots` turns it red.
- `ModuleContractRealAssemblyTests` walks the contract types. Adding a member that
  returns `InventoryItem` to `IInventoryModule` turns it red.
- `CompatibilityExceptionRealTreeTests` fails on an unregistered read of a General
  Inventory table. Removing `InventoryLotRepository` from `implementations`, or
  the probe's General Inventory row, turns it red.
- `InventoryModuleTests` pins the read copies field by field. Swapping
  `QuantityReceived` and `QuantityAvailable`, or `MeterStart` and `MeterEnd`,
  turns it red.
- `FeedUsageLockOrderTests` pins the order, as above.
