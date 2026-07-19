# Cluckwork glossary — key concepts

Domain concepts as the app uses them. For the full requirements see
[specs.md](./specs.md); for architecture see
[../technical/tech_spec.md](../technical/tech_spec.md).

## Birds & flocks

**Flock** — a group of birds managed as one unit, defined by breed and
*placement date* (the day the birds were placed in the house). Everything else
— production, mortality, sales — hangs off a flock.

**Placement date** — when the flock entered its house. Age (shown in weeks)
counts from here; laying performance is judged against age.

**Initial count** — birds placed on day one. Never changes with deaths; the
*bird ledger* accounts for those.

**Current birds** — `initial count − Σ bird movements`. Computed from the
ledger, never stored.

**Bird movement** — one row in the append-only bird ledger. Types:

- **Mortality** — birds that died on their own. Generated automatically when a
  daily entry is submitted; never entered by hand (that would double-count).
- **Cull** — birds deliberately removed alive: spent hens sold at end-of-lay,
  sick birds removed to protect the flock, poor layers. Always positive.
- **Adjustment** — count correction after a recount. May be negative (adds
  birds back). Corrections are always *new* rows — ledger rows are never
  edited or deleted.

**Spent hen** — a hen past her productive laying period (typically ~72–80
weeks); usually culled and sold for meat.

**Flock lifecycle** — `Active → Depleted → Archived` (archive is also allowed
straight from Active for mistake-created flocks):

- **Active** — laying; accepts daily entries and movements for any date.
- **Depleted** — birds are gone. Still visible in lists and history. Accepts
  *backfill* only: entries/movements dated on or before the depletion date
  (the final laying days are often entered late).
- **Archived** — hidden bookkeeping. Gone from pickers and the dashboard,
  visible on the Flocks screen behind a toggle; accepts nothing new. Historic
  data keeps rendering its name.
- **Reactivate** — the undo: a Depleted or Archived flock returns to Active.
  Its lifecycle dates are cleared, so full capture (any date) is restored. A
  flock archived after depletion carries both dates; reactivation clears both.

## Production

**Daily entry** — one flock's production record for one operational day: total
eggs, loss counts (cracked / dirty / discarded), mortality count, and the
graded breakdown of sellable eggs. One entry per flock per day (natural key).

**Daily entry lifecycle** — `Draft → Submitted → Locked`:

- **Draft** — editable; re-saving the same flock+date updates it in place.
  Nothing downstream exists yet.
- **Submitted** — the day is frozen. Submitting *generates* the downstream
  facts atomically: one **egg lot** per grade line, and a **mortality
  movement** if the count is > 0. Corrections after this need a manager
  adjustment (future slice).
- **Locked** — closed to any change (period close; future use).

**Sellable cap** — graded quantities must fit in
`total − cracked − dirty − discarded`. You cannot grade more eggs than
survived the day.

## Grading & stock

**Egg grade** — a per-farm grading bucket (spec §9.1). The seeded defaults are
sizes (Small/Medium/Large/Jumbo), qualities (Seconds/Cracked/Dirty/Soft
Shell), and custom buckets (Discarded/Internal Use), but the catalog is fully
user-managed. `gradeType` (Size/Quality/Custom) records which axis a bucket
is on and is immutable after creation.

**Saleable** — grades flagged saleable can receive graded production and be
sold on orders. Non-saleable grades are bookkeeping buckets — losses are
captured by the daily entry's counters, not grade lines. Names are unique per
farm, case-insensitively.

**Deactivated grade** — removed from capture and order pickers; existing
stock stays counted and order lines added before deactivation can still
confirm, but the grade cannot go on *new* order lines (`AddOrderItem`
rejects inactive grades) — reactivate to sell remaining stock. History keeps
resolving the name. Grades are never hard-deleted (historic rows reference
them forever).

**Egg lot** — a batch of sellable eggs: flock + production date + grade +
quantity. Created only by submitting a daily entry. Stock is the sum of lots'
available quantities per grade.

**FIFO allocation** — confirming a sale draws eggs from the *oldest* lots of
that grade first, under a pessimistic row lock so two sales can never
allocate the same physical eggs.

**Withdrawal restriction** — a lot flagged `RestrictedUntil` a date (bird
medication withholding periods). Restricted quantities show in stock but are
blocked from sale until the date passes. **Schema + enforcement only today:
no production path sets a restriction yet** — that arrives with medication
tracking (spec §13). Until then the system does not enforce withdrawal times.

## Feed & inventory

**Inventory item** — a catalog entry (spec §12.1): what a thing is, its
category (Feed/Supplement/Additive/Medication/…), and its **unit of
measure**. All categories support purchases, stock, and corrections; only
*flock feed usage* is limited to Feed/Supplement/Additive. The unit locks
once stock has been received — recorded quantities must never be silently
reinterpreted in a different unit. Names are unique per farm,
case-insensitively; items deactivate rather than delete (deactivation blocks
new purchases; remaining stock can still be used up).

**Inventory lot** — a received batch of an item: received date, quantity,
per-lot unit cost, optional supplier lot number/expiry. Created by recording
a **purchase**. Stock on hand for an item = the sum of its lots' remaining
quantities. Quantities are decimals (feed is weighed, not counted).

**Inventory movement ledger** — append-only audit trail (spec §12.3): every
purchase, usage, adjustment, and discard is a row with a signed quantity.
Rows are never edited or deleted — mistakes get a *compensating* row. Lots
hold the balance; the ledger explains it.

**Feed usage** — a feeding event: flock + date + item + quantity. Draws down
the item's lots **oldest first (FIFO)**, only from lots that existed on the
usage date, and records an estimated cost from the actual lots consumed
(feed-cost KPIs, spec §19). Usage records are create-only; only
Feed/Supplement/Additive items can be fed to a flock. Same lifecycle rule as
production: depleted flocks accept backfill up to their depletion date,
archived never.

**Adjustment / Discard** — the correction path for stock: a signed ledger row
against a specific lot (reason required). Negative fixes an over-entered
purchase or writes off spoiled feed (Discard); positive undoes an
over-recorded usage, capped at the lot's received quantity — genuinely new
stock is a purchase, not an adjustment. A lot can never go below what's
already been consumed.

**Water usage** — water consumed by a flock on a day (spec §12.5): direct
quantity in L/gal, or derived from meter start/end readings (the delta; when
both quantity and meters are given they must agree). No inventory behind
water, so records are **editable in place** (Version-guarded) rather than
corrected via compensating rows — the one create-then-edit record type.
Flock and date are fixed after creation. Same lifecycle gate as production
and feed.

## Sales

**Customer** — name + phone required; email/address/note optional. No
payments/balances yet (later 1.1 slice).

**Sales order lifecycle** — `Draft → Confirmed → Voided` (or `Draft →
Cancelled`): drafts are fully editable (add/edit/remove lines, cancel);
**confirming** allocates stock FIFO. A mistaken confirm is undone by
**voiding** — never by editing a confirmed order. (`Shipped`/`Invoiced`
exist in the status enum for later phases; nothing sets them yet, and only
`Confirmed` orders can be voided.)

**Void** — undo of a mistaken confirm (requires a reason): the allocated
quantities return to the *exact* egg lots they were drawn from (recorded at
confirm as lot-level allocations), preserving FIFO order and any withdrawal
restriction. The order stays listed as `Voided` with its lines and total —
this is not returns processing for delivered goods.

**Money** — stored as integer *minor units* plus a currency code snapshotted
from the account onto each order (JPY has 0 decimals, USD 2 — the snapshot
records that too). Totals are recalculated from lines on every mutation,
never incrementally patched.

## Cross-cutting

**Account** — the tenant. Every row carries `AccountId`; the API enforces
isolation with EF global query filters. Single-farm login today, multi-tenant
infrastructure dormant.

**Farm / House** — physical hierarchy. Present as ids from day one, but real
Farm/House management arrives with later phases; the MVP runs on one seeded
farm and house.

**Idempotency key** — every write request carries an `Idempotency-Key`
header. Retrying the same key replays the original response instead of
repeating the write — the safety net for flaky barn connectivity and the
backbone of the future offline mode (#50).

**Version (concurrency token)** — every mutable aggregate carries a `Version`
that each mutation bumps. Two concurrent edits: first save wins, second gets
a 409 and retries against fresh state. Append-only aggregates (bird
movements) don't need one.

**Operational day** — dates are farm-local calendar dates. For the MVP,
browser-local ≈ farm-local; true farm timezones are #35.

**Admin / Worker (#73)** — the two sign-in roles, and the only role
distinction before full RBAC. The dividing principle: anything that *records
the day's work* (entries, purchases, feed/water usage, flock and customer
creation, draft→confirm orders) is open to any signed-in user; anything that
*undoes, corrects, or configures* (void, stock/water corrections, flock
edit + lifecycle, culls/adjustments, grade and item catalogs, user creation)
requires the Admin role. The SPA hides gated controls; the API returns 403
with a problem body regardless. The role travels as a `role` claim in the
access token and is re-read at every token refresh.

**Users screen** — minimal admin-only user management (#73): create a user
with email, password, and role (Admin or Worker) and list existing ones.
Editing roles/passwords and house-scoped permissions belong to the RBAC
slice.
