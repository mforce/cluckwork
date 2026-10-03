# Put Commerce behind a contract and reach egg stock through a port (#854)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md),
> beside the #849 contract rule; this file records the Commerce contract, the
> stock port it calls, and the currency probe this slice inverted.

**Status:** accepted
**Date:** 2026-10-03

## What happened

No incident. This is epic #514, Track C slice 12, the sixth module behind a
contract after Finance (#849), Farm (#851), Flock Management (#852), General
Inventory (#855) and Egg Operations (#853). Commerce owns products, packed-unit
conversions, customers, sales orders, allocations and payments. Its confirm and
void are the system's most concurrency-sensitive writes.

Before this change, adapters reached Commerce through 57 parameter crossings of
37 non-contract types across 31 adapter rows: the product, customer, sale and
payment endpoints and both seeders. The count comes from running the #849
adapter check against `main` at d6dbd47d with a placeholder contract. After it,
the count is 0, and the matrix cell stays `A (31)`.

The design checkpoint found that nothing tested a confirm's lock order across
grades. `EggLotLockOrderTests` uses one grade, so locking each grade's lots in
line order, the shape `IEggLotRepository` warns deadlocks against a void, left
156 tests across 15 classes green. This slice adds a two-grade case.

## The rule

Commerce has two contract interfaces:

- `ICommerceModule` in `Cluckwork.Application.Features.Sales` is the adapter
  surface. It lists, creates, updates and activates products; lists and updates
  conversions; creates, updates, searches and reads customers; drafts, confirms,
  voids, cancels and reads orders; and records, voids and reads payments. The
  endpoints and both seeders call it.
- `IEggUnitConversionLookup` is the packed-unit read port. Farm's settings
  handler and Access's stepper-unit handler read through it.

Reads return `ProductDetails`, `EggUnitConversionDetails`, `CustomerDetails`,
`SalesOrderDetails` with `SalesOrderItemDetails`, `SalesOrderListItem` and
`PaymentDetails`, plus the existing `CustomerReference` and `CustomerBalance`.
`CommerceModule` only forwards to the existing handlers and five repositories, so
the SQL does not change. `owners.Commerce.contract` lists 34 types; the six
Commerce repositories are its `implementations` under #850.

## The stock port

Commerce reaches egg lots through Egg Operations' `IEggStock`, never through
`IEggLotRepository`, and never sees `EggLot`.

- `LockForSaleAsync` takes the confirm's one `FOR UPDATE` over every available
  lot of the order's grades, farm-wide, in `(ProductionDate, Id)` order, and
  returns an `IEggStockReservation`.
- `IEggStockReservation.Plan` is the FIFO planner over those locked lots. It is
  pure, so `ConfirmSaleHandler` can plan over a restricted Worker's assigned
  flocks first and over every locked lot second without a second lock (#612).
- `IEggStockReservation.DrawAsync` calls `EggLot.Allocate` on the locked
  instance and writes the Sale movement. A refusal throws: the plan read the
  same instance.
- `IEggStock.RestoreAsync` locks the void's source lots by id in the same order,
  refuses `EggLot.AllocationSourceMissing`, restores each lot and writes its
  Void movement.

The port never saves, like `IMortalityLedger` (#852). Commerce keeps the order
lock, the #612 policy, the refusal messages, the allocation rows and their ids,
and the audit row. Egg Operations keeps the lock, the planner, lot mutation and
the ledger rows. The reference type on a movement is passed in, so Egg
Operations names no Commerce type.

The handlers run the same steps in the same order. One step moved: a draw's
`SalesOrderAllocation` is now created before `Allocate` runs, because the Sale
movement needs its id. Both are in memory until the one save, and EF orders that
save's batch itself. A trace of every SQL statement and transaction event, taken
on `main` and on this branch, is byte-identical for a confirm, a void, a short
confirm and a payment-gated void.

`SaleAllocationPlanner`, `SaleAllocationPlan` and `PlannedEggLotDraw` moved to
Egg Operations. The planner now takes `SaleDemandLine`s, not order items, so
`SaleAllocationPlannerTests` builds its input differently; its assertions are
unchanged.

## The currency probe

Farm's currency rule asks whether anything holds an amount in the farm currency.
`CurrencyBoundRowProbe` used to read Commerce, Finance and General Inventory
tables itself, which made it two compatibility exceptions. Now Farm owns
`ICurrencyBoundRowSource`, and each repository that owns a probed table
implements it with the probe's query for that table. The probe asks the sources
in registration order, which is the order it read the tables in before, and
stops at the first yes. Both exception rows are gone.

Why not a read on each module's contract: Farm would then resolve
`ICommerceModule` and its handlers on every `IFarmModule` use, and it would gain
Farm to Finance and Farm to General Inventory edges, closing cycles with the
existing Finance to Farm and General Inventory to Farm edges. The sources sit in
Infrastructure, the Platform hub, so the inversion adds no module edge.

## What this does NOT cover

- Peer modules are not checked (#849, #852). A peer that injects
  `IEggUnitConversionRepository` again stays green.
- `ConfirmSaleHandler`'s in-transaction role and assignment reads stay a
  declared Commerce to Access edge for #857. `AccountProvisioner` still inserts a
  new farm's default conversions, a declared Access to Commerce edge (#857).
- Seeder reads. Eight seeder methods read Commerce tables to clean up a partial
  demo seed, count the manifest and converge on a re-run. They are compatibility
  exceptions that #858 deletes; the contract does not grow reads only a seeder
  needs.
- Insights reads Commerce tables through its declared edge.
- `EggLot.AllocationSourceMissing` has no test and cannot be reached:
  `FK_SalesOrderAllocations_EggLots_EggLotId` stops a drawn lot being deleted,
  and void is AdminOnly, so the flock scope never hides one.
- Method bodies. `ICommerceModule.SalesOrderAuditEntityType` exists for the
  endpoints' provenance reads.

## How it is enforced

- `AdapterReachRealTreeTests` reports a contract bypass for any adapter that
  takes a non-contract Commerce type. Adding `ISalesOrderRepository orders` back
  to `SaleEndpoints.GetSalesOrder` turns it red.
- `ModuleContractRealAssemblyTests` walks the contract types. A member returning
  `SalesOrder` on `ICommerceModule`, or an `EggLot` list on
  `IEggStockReservation`, turns it red.
- `CompatibilityExceptionRealTreeTests` fails on an unregistered read of a
  Commerce table. Removing `PaymentRepository` from `implementations` turns it
  red, its currency-source read included.
- `EggLotLockOrderTests.ConfirmSale_TwoGrades_HoldsTheOtherGradesEarlierLotWhileWaitingOnTheLater`
  turns red when the port locks each grade in line order.
- `ConfirmSaleAtomicityTests` faults a confirm after a lot allocation, after its
  Sale movement, after the allocation rows and after the order is confirmed, and
  finds nothing persisted. Removing the confirm's transaction alone, or saving
  after each draw alone, leaves it green: one holds the other. Both together
  turn all four cases red.
- `CommerceModuleTests` pins the copies. Swapping `Email` and `Address`,
  `Quantity` and `QuantityBase`, or `ReferenceNumber` and `Note` turns it red.
- Unregistering `ProductRepository` as a currency source turns
  `FarmSettingsTests.CurrencyChange_AfterAPricedProductExists_IsRefused` red.
