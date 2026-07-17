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
stock keeps selling until the lots drain, and history keeps resolving the
name. Grades are never hard-deleted (historic rows reference them forever).

**Egg lot** — a batch of sellable eggs: flock + production date + grade +
quantity. Created only by submitting a daily entry. Stock is the sum of lots'
available quantities per grade.

**FIFO allocation** — confirming a sale draws eggs from the *oldest* lots of
that grade first, under a pessimistic row lock so two sales can never
allocate the same physical eggs.

**Withdrawal restriction** — a lot flagged `RestrictedUntil` a date (bird
medication withholding periods). Restricted quantities show in stock but are
blocked from sale until the date passes.

## Sales

**Customer** — name + phone required; email/address/note optional. No
payments/balances yet (later 1.1 slice).

**Sales order lifecycle** — `Draft → Confirmed` (or `Draft → Cancelled`):
drafts are fully editable (add/edit/remove lines, cancel); **confirming**
allocates stock FIFO and is the point of no return for inventory.

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
