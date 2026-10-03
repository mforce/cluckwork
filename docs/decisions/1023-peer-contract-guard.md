# Reach a contracted peer module only through its contract or seam (#1023)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md),
> beside the #849 contract rule; this file records the peer check, Farm's seam,
> type claims, and what the check deliberately leaves to review.

**Status:** accepted
**Date:** 2026-10-03

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
in that module's `contract` or `seam` in `module-ledger.json`. The check reads
parameter types and service resolutions, the same syntax #849 reads for adapters.

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
  No owner claims a type yet.
- Its own module, Platform code, and modules with no contract are not checked.
  Today those are Access, until #857 declares its contract, and Insights.

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

## What this does NOT cover

- Method bodies: `nameof`, static calls, object creation, `Set<T>()`, local
  declarations, casts and patterns.
- Return, property and field types.
- A module type passed through a Platform type. Platform is the free hub, so a
  Platform helper may take `IFlockRepository` and a peer may take the helper.
- Untyped lambda parameters, `var`, reflection, and service types held in
  variables.
- Modules with no contract.
- Access types left in Platform on purpose: `ICurrentUser`, `IFlockScopeGuard`
  and `SystemActors`.
- The #842 edge walk reads namespaces. It sees a claimed type only through a
  fully qualified name, not through an import and a short name.
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
  imports only.
- Against 7bb45cc3, each of these turned the real-tree test red: adding
  `IFlockRepository` to `CreateExpenseHandler`, adding a fully qualified
  `IBirdMovementRepository` to `AdjustExpenseHandler`, removing
  `IAccountRepository` from Farm's seam (12 bypasses), and keeping only
  Application namespaces as roots (`walked 612 adapters, expected at least 800`).
  Adding `IFlockRepository` to `CreateFlockHandler` stayed green. Adding
  `IAccountRepository` to an expense endpoint turned `AdapterReachRealTreeTests`
  red, so the seam does not reach adapters.
