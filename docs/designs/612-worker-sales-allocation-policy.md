# Design — #612 Worker sale-allocation policy

**Status:** Approved direction; implementation pending
**Baseline:** `origin/main` at `93a1c7260e420f0c5d638042b9c1405ae6f63b6f`

## Goal

Add one farm setting controlling how a restricted plain Worker allocates eggs
when confirming a sale:

- `AssignedFlocksOnly` — default for existing and new farms.
- `AllFarmFlocks` — explicit Owner/Manager opt-in.

Owner, Manager, and Sales confirmations remain farm-wide. ReadOnly cannot
confirm. A Worker with no concrete restriction remains farm-wide.

## Contract

### Setting

- Store `WorkerSaleAllocationPolicy` on `Account` as a required bounded string.
- Add one forward migration; never edit `InitialCreate`.
- `Account.UpdateSettings` accepts the value and bumps `Version`.
- `GET/PUT /api/v1/account/settings` expose the named enum.
- Existing and new rows default to `AssignedFlocksOnly`.

### Roles and assignments

- Only a plain Worker is affected by flock assignments.
- Owner, Manager, Sales, ReadOnly, and unknown/Denied roles ignore retained
  assignment rows. This intentionally makes Sales/ReadOnly reads farm-wide;
  their route permissions do not change.
- Assigning a flock to a live non-Worker returns
  `Users.FlockAssignmentsWorkerOnly`. Removing retained rows remains allowed.
- Promotion retains rows but makes them inactive; demotion to Worker reactivates
  them. Assignment writes are not newly serialized.
- Preserve the existing raw role string in Users API responses. Use one
  effective-role resolver internally for authorization/scope decisions.

### Confirmation

Inside the existing transaction:

1. Lock and validate the current Account with `FOR SHARE`.
2. Derive the farm date from the locked Account timezone.
3. Lock and freshly load the SalesOrder and items with `FOR UPDATE`.
4. Check `SalesOrder.CheckCanConfirm()` before touching stock. `Confirm()` must
   delegate to the same precondition.
5. Re-read the caller's effective role. Map a now-forbidden caller to HTTP 403.
6. For a plain Worker, read committed assignments immediately before stock.
7. Call the existing farm-wide FIFO lot-lock query exactly once, ordered by
   `(ProductionDate, Id)`.
8. Plan the whole order in memory without mutating aggregates.
9. Under `AssignedFlocksOnly`, try assigned lots first. If that fails, retry the
   plan against the same locked farm-wide rows:
   - farm-wide succeeds → `EggLot.AssignedFlocksInsufficientStock` (422);
   - farm-wide fails → existing `EggLot.InsufficientStock` (422).
10. Apply only a successful plan, then confirm and audit.

The order lock keeps Confirm and Void in the same SalesOrder → EggLots order.
Do not pre-track SalesOrder/EggLot before locked reads; EF locking queries do
not refresh an existing identity-map instance.

### Privacy and UI

- A restricted Worker never receives grade/quantity/flock facts derived from
  unassigned lots. Both restricted-Worker failure descriptions are generic.
- `/account` exposes only `showFarmWideSaleAllocationNotice` for the persistent
  generic Sales notice.
- Settings shows the two policy choices.
- Users shows retained elevated-role assignments as inactive/removable, with
  add disabled. The Users route remains Owner-only.
- Update en/es/tl, the Help guide and in-app glossary, and
  `specs/product/GLOSSARY.md`.

## Required proof

- Default assigned-only and opt-in farm-wide allocation.
- Whole-order planning handles repeated grades and never mutates on failure.
- Assigned failure/farm success returns the distinct 422; farm failure retains
  the existing code without leaking unassigned-stock detail.
- Account → SalesOrder → EggLots ordering survives confirm/void and
  same-order-confirm races without duplicate allocations.
- Policy and role changes serialize through the Account lock.
- Assignment add/remove follows the live committed read without new locks.
- `NotDraft`, `NoItems`, 403, and stock failures leave exact SalesOrder/EggLot
  values, versions, and EF states unchanged.
- Migration default/backfill/down, schema docs, settings callers, UI fixtures,
  simulation UI, and all three locales remain in sync.

## Accepted cost and non-goals

The Account and lot locks remain held until the request-wide idempotency
transaction commits. A queued settings/Identity writer can briefly queue later
confirmations; this is accepted for policy/role linearization. Current k6 does
not exercise confirmation, so do not claim performance evidence from it.

No seller-selected source flock, assignment-write serialization, unlocked
stock preflight, `account_id` hardening, role-policy widening, or change to
withdrawal/date/grade eligibility is included.
