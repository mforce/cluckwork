# Put farm settings behind a Farm contract; keep identity and the account seam outside it (#851)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md),
> beside the #849 contract rule; this file records what the Farm contract covers
> and what it deliberately leaves alone.

**Status:** accepted
**Date:** 2026-10-02

> **Amended 2026-10-03 (#858 P3, PR #1043):** the contract now also lists
> `IFarmDirectory`, `FarmTimeZone` and `FarmListing`. Unlike every member below,
> the directory reads every farm with the tenant filter off, for the operator
> verbs and the lock sweep only. `DailyEntryLockSweep.RunAsync`,
> `AccountSlugLookup.ResolveAsync` and `ListAccountsCliCommand.RunAsync` read
> through it and are no longer compatibility exceptions. #858 P8 deleted the
> forwarder and records the final state in
> [`858-platform-composition.md`](858-platform-composition.md).

## What happened

No incident. This is epic #514, Track C slice 9, the second module behind a
contract after Finance (#849). It follows the shape in
[`849-module-contract.md`](849-module-contract.md) unchanged.

Before this change, adapters reached Farm through 23 parameter crossings of 11
non-contract types across 18 adapter rows: the account, settings, logo and banner
endpoints, the expense list and customer-balance currency reads, the simulation
seeder, and sign-in. The count comes from running the #849 adapter check against
`main` with a placeholder Farm contract.

## The rule

`IFarmModule` in `Cluckwork.Application.Features.Accounts` is Farm's contract. It
reads the current farm's settings as `FarmSettingsDetails`, reports whether the
currency may still change, writes settings through `UpdateFarmSettingsCommand`, and
reads, sets and removes the logo and banner. `owners.Farm.contract` in
`module-ledger.json` lists the interface and the five records and commands it
carries. Every member acts on the current tenant's farm, so it runs only after
`TenantContext` is resolved.

## Three things stay outside the contract

**Sign-in.** Login resolves a farm code with no tenant, which is identity
establishment, not a settings read. Routing it through `IFarmModule` would either
run a tenant-filtered read before `TenantContext` exists or need a filter bypass
inside the contract (#530, #562, #673). It goes through
`IIdentityProvider.ResolveFarmCodeAsync`, a Platform port that `IdentityProvider`
implements with the `IAccountRepository` it already holds for the account-scoped
login. The query, the order of checks (farm code, then suspension, then
credentials), the error codes and the statuses are unchanged. Login now reaches
Access only.

**The farm clock.** `FarmClock` keeps reading the timezone through
`IAccountRepository`. It lives in `Cluckwork.Infrastructure.Time`, which is the
Platform hub and may reference any module, so no guard sees it. It also sits
inside the account seam below. Switching it would construct `FarmModule` and its
handlers on every dated request and buy no check.

**The account seam for peer modules.** `IAccountRepository` and `Domain.Accounts`
stay the stable seam that Access, Commerce, General Inventory, Finance, Egg
Operations and Flock Management call directly. The locked currency snapshot
(`GetCurrentSharedLockedAsync`, the #162 `FOR SHARE` protocol), the role
vocabulary, the assignment rows, the account lifecycle writes and
`SeedDefaults` do not move behind `IFarmModule`. The #849 pilot introduced no
currency port, so there was none to adopt. Folding them in later would reopen
this contract once the Access slice runs, and the #849 check covers adapters,
not peer modules, so it would enforce nothing there.

## Farm -> Commerce stays a declared edge

`UpdateFarmSettingsHandler` still resolves the stepper unit through Commerce's
`IEggUnitConversionRepository`, and `UpdateFarmSettingsValidator` still parses the
discount ceiling with `DiscountCeiling.TryParsePercent`. Commerce has no contract,
so there is nothing to call instead. The ledger row already declared both; it now
also names `FarmSettingsDetails`, which carries `Domain.Catalog.EggUnit` out to
the contract's callers.

## What this does NOT cover

- Method bodies. The adapter walk reads parameter types and service
  resolutions. Endpoints still name `Roles`, `WorkerSaleAllocationPolicy`,
  `ImageSanitizer.TooLarge` and `SeedDefaults` inside bodies. All are
  `Domain.Accounts` or `Domain.Media` vocabulary inside the account seam.
- Direct EF reads of the account row outside Farm. Insights reads the report
  currency in `ReportQueries.AccountCurrencyAsync`. The other readers are
  `DailyEntryLockSweep.RunAsync`, `MissingBaseDataAsync` in both seeders,
  `SimulationDataSeeder.SeedSecondAccountAsync` and `ComputeCountsAsync`, and in
  Api `AccountSlugLookup.ResolveAsync`, `ListAccountsCliCommand.RunAsync` and
  `CredentialEpochMiddleware.InvokeAsync`. #850 registers them as compatibility
  exceptions.

## How it is enforced

- `AdapterReachRealTreeTests.RealSourceTree_EveryAdapterReachIsDeclared` reports
  a contract bypass for any adapter that takes a non-contract Farm type. Adding
  `IFarmLogoRepository logos` back to `FarmLogoEndpoints.GetLogo` turns it red.
- `ModuleContractRealAssemblyTests` walks the Farm contract types. Adding
  `Task<Account?> LeakAsync(CancellationToken ct)` to `IFarmModule` turns it red.
- `FarmModuleTests` pins the settings copy field by field. Swapping
  `DateFormatOverride` and `TimeFormatOverride` in the copy turns it red.
