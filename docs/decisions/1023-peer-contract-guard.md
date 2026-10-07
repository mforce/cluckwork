# Reach a contracted peer module only through its contract or seam (#1023)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md),
> beside the #849 contract rule; this file records the peer check, Farm's seam,
> type claims, and what the check deliberately leaves to review.

**Status:** accepted
**Date:** 2026-10-03
**Mechanism note (2026-10-04, #859):** the ledger rows now live in the `RealModuleLedger.*.cs` files in `tests/Cluckwork.Application.Tests/Architecture`, not in a JSON file. The rule and its checks are unchanged.

## What happened

No incident. This is epic #514, Track C. The contract slices (#849, #851, #852,
#853, #854, #855) check adapters only. #1030 added one peer check, for Commerce's
reach into Egg Operations. Nothing else checked module-to-module calls. After
#852, no handler outside Flock Management injects `IFlockRepository`, but a peer
handler that injected it again stayed green.

The check was deferred because Farm keeps `IAccountRepository` and
`Domain.Accounts` outside its contract on purpose, as the account seam peer
modules call directly (#851). A blanket rule would have failed on that seam. The
ledger needed a per-owner list of the extra types peers may reach first.

Measured on `main` at 7bb45cc3, the walk visits 936 module members and finds no
violation. 15 references reach Farm's seam: 12 to `IAccountRepository` from
Access, Commerce, Finance and General Inventory, and 3 from Access to `Account`
and `UserRoleAssignment`.

## The rule

A module member may take another contracted module's type only when the type is
in that module's `Contract` or `Seam` in `RealModuleLedger.Owners`. The check reads
parameter types and service resolutions, the same syntax #849 reads for adapters.
That includes service resolutions and typed lambda and local-function parameters
inside a body, but nothing else in the body.

- **`seam`** lists the types peer modules may reach outside the contract. Farm's
  seam is `IAccountRepository`, `Domain.Accounts.Account` and
  `Domain.Accounts.UserRoleAssignment`. Adapters still reach Farm only through
  its contract, and `ModuleContractRealAssemblyTests` does not walk the seam,
  because the seam holds aggregates on purpose.
- **`types`** lets a module claim a type that sits in a Platform namespace. The
  claim joins the owner index as an exact claim on the type's full name, and
  `Resolve` probes the full name first, so the claim outranks the namespace's
  owner wherever a full type name is resolved: the adapter walk, this walk and
  contract validation. A claimed type is also walked as a member of its module.
  Access claims the two Common identity ports described below.
- **Entries name only top-level, non-generic types.** This holds for `contract`,
  `seam` and `types`, and a claimed type may not declare nested types. Each
  breach is a registry error. The syntax walk drops generic arity from both
  declarations and references, so a `seam` entry for `IAccountRepository` would
  also admit an unlisted `IAccountRepository<T>`. A claim on a generic type would
  be counted under one key and resolved under another. A claim's nested types
  would belong to the module but never be walked. Rejecting those shapes is
  smaller than carrying arity and nesting through every key, and nothing needs
  them today. #1013 hit the same arity loss in the compatibility-exception
  scanner.
- Its own module, Platform code, and modules with no contract are not checked.

Break it and a peer that injects another module's repository passes CI, and the
contract stops meaning anything between modules.

## Why not the obvious alternative

**A blanket constructor check over Application.** It fails on the 15 seam
references above. Putting the seam in the contract instead would let adapters use
it and would fail the contract walk, which forbids aggregates.

**All of `Domain.Accounts` as the seam.** #851's prose names the namespace. The
owner chose three types instead. A new `Domain.Accounts` type reached by a peer
then fails until someone lists it, which is a reviewed one-line ledger change.

**Read return, property and field types too.** Measured on 7bb45cc3, that adds one
hit: `Account.MaxDiscount` returns Commerce's `DiscountCeiling`, a crossing #851
already declares. Taking it would mean adding `DiscountCeiling` to Commerce's
contract or an exemption, for no new catch.

**Read method bodies too.** That adds 25 hits, all accepted crossings: 21 entity
reads in Insights' `ExportQueries` under declared edges, `nameof(Flock)` in three
daily-entry handlers, the `DiscountCeiling` and `FarmSettingsRules` static calls,
and the provisioner's default grade and conversion inserts. A body rule needs an
exemption list from the first day, and a syntax walk without binding mistakes
member names for types. Each extra layer would buy exemptions, not catches.

**A seam entry for Access's types in `Application.Common`.** `IIdentityProvider`
and `IStepUpGrantService` sit in the Platform hub, so any module may call them
and a seam row would not change that. A Platform type needs an owner first,
which is what `types` gives it.

## Access: the claim waits for #857

Access will claim `IIdentityProvider` and `IStepUpGrantService` in a follow-up
after #857 declares Access's contract. Before that, the claim enforces nothing
between modules. On 7bb45cc3 it would also add 4 adapter rows and change the
coupling matrix, for code #857 replaces anyway. With a placeholder Access
contract, the claim produced 2 peer violations, both in `ConfirmSaleHandler`.
#857 moves both reads to `IAccessLookup`. The owner decided that #857's last
step moves `SimulationDataSeeder`'s `CreateUserAsync` onto `IAccessOperations`,
so the seeder does not become an adapter bypass once Access owns
`IIdentityProvider`.

## Access claims in #857 E2

#857 E2 folds the planned follow-up into the final Access declaration. Access
now claims `IIdentityProvider` and `IStepUpGrantService` individually, and
SimulationDataSeeder creates cast users through `IAccessOperations`. The
real-tree peer probe rejects a Finance member taking either Common port; the
claim-presence assertions reject deleting either ownership entry. Access has a
28-entry aggregate-free contract, and its adapters pass the same walk. The
previous measurement and deferral above describe the pre-contract baseline.

## What this does NOT cover

- Method bodies: `nameof`, static calls, object creation, `Set<T>()`, local
  declarations, casts, patterns and parameter default values.
- Return, property and field types.
- A module type passed through a Platform type. Platform is the free hub, so a
  Platform helper may take `IFlockRepository` and a peer may take the helper.
- Untyped lambda parameters, `var`, reflection, and service types held in
  variables.
- Modules with no contract.
- Access types left in Platform on purpose: `ICurrentUser`, `IFlockScopeGuard`
  and `SystemActors`.
- The #842 edge walk reads namespaces. It attributes an incoming reference to a
  claimed type only when the reference is fully qualified, and it attributes the
  claimed type's own outgoing references to Platform, because it classifies a
  source by its namespace. The #850 scanner also classifies a reader by its
  namespace, so a claim grants its reads no own-module allowance.
- The seam does not excuse an undeclared edge. A Commerce handler that newly takes
  `IAccountRepository` passes this check and still needs its symbol in the
  `Commerce -> Farm` edge.

## How it is enforced

- `PeerContractRealTreeTests` runs `AdapterReachScanner.ScanPeers` over `src/`
  and fails on any bypass. It also requires at least 800 walked members and two
  references that must stay visible: `ConfirmSaleHandler.ctor` to `IEggStock`, and
  `CreateExpenseHandler.ctor` to `IAccountRepository`.
- `PeerContractTests` covers each allowance and each registry error on fixtures.
  `ModuleLedgerTests.GlobalImportOfAPlatformNamespaceHoldingAClaimedType_IsAFailure`
  covers the global-import ban, which now includes any namespace that holds a
  claimed type, because the walk resolves a short name through its own file's
  imports only. `GenericHomonymOfAnEntry_IsARegistryError` and
  `GenericOrNestedClaim_IsARegistryError` cover the entry shapes.
- Against 7bb45cc3, each of these turned the real-tree test red: adding
  `IFlockRepository` to `CreateExpenseHandler`, adding a fully qualified
  `IBirdMovementRepository` to `AdjustExpenseHandler`, removing
  `IAccountRepository` from Farm's seam (12 bypasses), and keeping only
  Application namespaces as roots (`walked 612 adapters, expected at least 800`).
  Adding `IFlockRepository` to `CreateFlockHandler` stayed green. Adding
  `IAccountRepository` to an expense endpoint turned `AdapterReachRealTreeTests`
  red, so the seam does not reach adapters.
- Review round 1 of PR #1033 found four shapes that passed every guard: a generic
  homonym of a seam type, a generic claimed type, a claimed type with a nested
  class, and a nested claim behind a global import. Each now fails
  `PeerContractRealTreeTests` with a registry error naming the entry.

## Amendment, 2026-10-06: no type claims (#1087)

Access's two identity ports moved into Access's own namespace (`Application/Modules/Access/Users`, #1095), so no owner claims a type in a Platform namespace any more. The `types` list, its registry checks and the analyzer's reading of it are deleted (#1099); the claim-related failures listed above no longer exist. The seam (`Seam`) and the peer walk are unchanged.
