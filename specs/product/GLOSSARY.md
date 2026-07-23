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

## Getting around

**Navigation** — where the app's screens live. Above 900px wide they sit in the
aubergine **sidebar**, grouped by job (Overview, Production, Sales & stock,
Insights, Setup, Help). Below 900px — a phone — the sidebar gives way to a
**bottom tab bar**: the four most-used destinations the current role can reach,
plus **More**, which opens a sheet with the complete grouped list. The four
tabs are chosen per role from a fixed priority order (Daily entry, Stock, Sales,
History, …), so a worker leads with Daily entry and a ReadOnly viewer, who has
neither production nor sales, leads with Stock. Both navs are built from one
model, so the role gates cannot drift between them.

**"Something went wrong" screen** — the error-boundary fallback. If a screen
throws while rendering, the app catches it and shows this — a short message,
**Reload**, and **Back to the dashboard** — instead of unmounting to a blank
page (the failure mode #138 hit on a phone). The screen boundary keeps the nav
shell around it, so the rest of the app stays reachable; a second boundary wraps
the whole app — outside the auth and router providers — for the rarer case where
the shell or that setup itself throws. The error text
sits under a collapsed **Error details** for a support screenshot. It catches
render/lifecycle throws only — event-handler and network failures still surface
as each screen's own inline error.

## Production

**Daily entry** — one flock's production record for one operational day: total
eggs, loss counts (cracked / dirty / discarded), mortality count, and the
graded breakdown of sellable eggs. One entry per flock per day (natural key).

**Daily entry steps (#134)** — the capture screen is two numbered panes side by
side: **1 Egg counts** and **2 Grading**. Flock and date sit above them as
context, not as a step — they say *which* day is being recorded, not part of
recording it. The two panes reconcile: the **sellable** figure the counts
produce is the target the grades have to hit, so they are placed where both can
be read at once.

**Left to grade (#134)** — grading counts **down** to zero rather than reporting
*graded n of m*. The figure beside the grades is how many sellable eggs are
still unaccounted for; it turns green when the day adds up exactly and red when
the grades overshoot the sellable count. Submitting is blocked while it is over.

**Entry footer (#134)** — both saves sit in a bar pinned to the bottom of the
screen, along with save messages: anything below a pinned bar scrolls underneath
it and is never read. On a phone the two panes stack and scroll away, so the bar
also carries a compressed *n sellable · n left*; on a wider screen both panes are
already visible and repeating it would be noise.

**Steppers (#134)** — every count and grade field has **−** and **+** buttons
either side. Holding one repeats, widening its stride from 1 to 5 to 10, so a
few hundred eggs takes about a second. Built for the barn: the browser's own
spinner is a ten-pixel target and disappears entirely on touch.

A grade's **+** stops once the day is fully graded — the guided control will not
build an over-graded entry. Typing still can: a draft is allowed to be over
while it is being rearranged, and only submitting is blocked.

**Put all in… (#134)** — hands the entire remainder to one grade in a single
move, for the commonest last step of the day ("and the rest are Large"). Drag it
onto a grade, or tap it and pick one. Both routes exist deliberately: dragging
is unavailable on a phone and unreachable from a keyboard, so it is never the
only way. It disappears when nothing is left to place.

**Editing draft (#134)** — a badge beside the title when the day being captured
already has a saved **Draft**, so re-opening work in progress is distinguishable
from starting a fresh day. Only locked days carried a signal before.

**Daily entry lifecycle** (#69) —
`Draft → Submitted → Locked → ManagerAdjusted / Voided`:

- **Draft** — editable; re-saving the same flock+date updates it in place.
  Nothing downstream exists yet. History links Draft rows straight back to
  the entry form via **edit** (#85) — open to workers, unlike adjust/void.
- **Submitted** — the day is frozen for workers. Submitting *generates* the
  downstream facts atomically: one **egg lot** per grade line (each lot
  carries a `DailyEntryId` link back to its entry), and a **mortality
  movement** if the count is > 0.
- **Locked** — stamped automatically once a submitted entry is strictly
  older than 7 farm-local days (spec §8.1 default; background sweep).
  Locked ≠ untouchable: admins can still adjust or void.
- **ManagerAdjusted** — an admin corrected the totals/grades of a
  submitted or locked entry (reason required). The correction reconciles
  the entry's egg lots in the same transaction — grown, shrunk, added, or
  emptied — but **never below what a lot already sold**, and appends a
  compensating bird movement for any mortality change. The replaced values
  are kept as an audit snapshot on the entry until the audit log lands.
  Adjusting again is allowed; each adjust snapshots what it replaced.
- **Voided** — an admin undid the whole entry (reason required): every lot
  it generated is emptied (refused if any of its eggs were sold), the
  day's mortality is reversed by a compensating movement, and the entry is
  preserved as Voided. Mirrors the sales void (#60): compensating rows,
  never deletions. Voiding **vacates the day** (#82): the same
  house/flock/date can be recorded again as a fresh entry — the natural-key
  uniqueness is enforced only across live (non-voided) entries. Entries
  submitted before lot-to-entry tracking existed can't prove which lots are
  theirs and refuse adjust/void.

**Sellable cap** — graded quantities must fit in
`total − cracked − dirty − discarded`. You cannot grade more eggs than
survived the day.

## Grading & stock

**Egg grade** — a per-farm grading bucket (spec §9.1). The seeded defaults are
sizes (Small/Medium/Large/Jumbo), qualities (Seconds/Cracked/Dirty/Soft
Shell), and custom buckets (Discarded/Internal Use), but the catalog is fully
user-managed. `gradeType` (Size/Quality/Custom) records which axis a bucket
is on and is immutable after creation.

**Egg movement ledger (#101)** — every change to a lot's available quantity
is an explicit, append-only signed row (spec §9.4): `Production` when a
submitted entry creates the lot, `Sale` per confirmed allocation, `Void` when
a sale or entry void returns/vacates eggs, `Adjustment` for manager
corrections. Written in the same transaction as the lot change, so the cached
`QuantityAvailable` always equals the sum of the lot's movements — the
tech-spec rule that cached balances must be rebuildable from ledgers.

**Saleable** — grades flagged saleable can receive graded production and be
sold on orders. Non-saleable grades are bookkeeping buckets — losses are
captured by the daily entry's counters, not grade lines. Names are unique per
farm, case-insensitively.

**Product (#97)** — what the farm sells (spec §10.1). Phase 1 supports egg
products only: each maps to exactly one egg grade (sales will draw from that
grade's lots), carries a selling unit (dozen/tray/carton/…), an optional
default price, and a currency snapshotted from the account at creation. The
product type is immutable; deactivation hides a product from pickers while
history keeps it. Names are unique per account, case-insensitively.

**Packed unit (egg unit conversion) (#97)** — how many individual eggs a
selling unit holds (spec §9.7). No fixed factors: a carton is 12, 18, or 30
eggs depending on the market, so each account defines its own (defaults:
dozen 12, flat/tray 30, carton 12, case 360; individual is always 1 and
immutable). Sales lines will snapshot the factor at line creation — redefining
a unit never reinterprets recorded orders.

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
payments and balances (spec §10.11, shipped #89).

**Sales order lifecycle** — `Draft → Confirmed → Voided` (or `Draft →
Cancelled`): drafts are fully editable (add/edit/remove lines, cancel);
**confirming** allocates stock FIFO. A mistaken confirm is undone by
**voiding** — never by editing a confirmed order. (`Shipped`/`Invoiced`
exist in the status enum for later phases; nothing sets them yet, and only
`Confirmed` orders can be voided.)

**Sales line (#99)** — sells a **product** in a packed unit. At line creation
the line snapshots the product type, the grade the product mapped to, and the
unit's eggs-per-unit factor (`base_unit_factor`, spec §10.5/§9.7); `quantity`
is selling units, `quantity_base = quantity × factor` is individual eggs —
allocation and the stock guard run on `quantity_base`. Re-pointing a
product's grade or redefining a packed unit only affects future lines, never
recorded ones. Price is per selling unit, prefilled from the product's
default and editable per line.

**Void** — undo of a mistaken confirm (requires a reason): the allocated
quantities return to the *exact* egg lots they were drawn from (recorded at
confirm as lot-level allocations), preserving FIFO order and any withdrawal
restriction. The order stays listed as `Voided` with its lines and total —
this is not returns processing for delivered goods.

**Money** — stored as integer *minor units* plus a currency code snapshotted
from the account onto each order (JPY has 0 decimals, USD 2 — the snapshot
records that too). Totals are recalculated from lines on every mutation,
never incrementally patched.

**Audit log (#93)** — append-only record of every corrective, destructive,
or configuration change (the admin-gated action set): actor (user id +
email snapshot), UTC timestamp, action code, entity reference, the reason
where the command carried one, and a small details payload. Domain data,
not telemetry (tech spec): written **in the same transaction** as the
change — a rollback erases the event with the change, and there is no
update or delete surface anywhere. Admin-only viewer at /audit. The
entity-local snapshots (`AdjustedFromJson` etc.) remain — they are the
record's own history; the audit log is the cross-cutting trail.

**Hen-day % (#91)** — eggs collected ÷ hen-days × 100 (spec §19.3). A
hen-day is one bird alive for one day; the day's bird count comes from the
bird ledger (placements + movements). The production report shows it per
day and for the whole period (period eggs ÷ period hen-days — not an
average of daily percentages).

**Production report (#91)** — per-day official production over a range
(Draft entries aren't submitted, Voided ones vacated their day — neither
counts): eggs, losses, sellable, deaths, hen-day %, period totals, grade
breakdown. Open to workers — it is their own recorded work.

**Profit, basic (#91)** — confirmed order revenue − recorded expenses for
the range, both operands shown. Deliberately no COGS or inventory
valuation; those belong to a later accounting slice. Admin-only, as are
the sales and expense summaries.

**Payment (#89)** — money in against a **confirmed** sales order (spec
§10.11): date, amount in minor units, method (cash / check / card / bank
transfer / mobile payment / other), optional reference and note. The
currency copies from the ORDER at creation. Partial payments are normal;
**overpaying the outstanding amount is refused** (checked under the order's
row lock, so racing payments can't overshoot). A wrong payment is **voided**
with a reason — never deleted — and the outstanding grows back. An order
with non-voided payments refuses to void ("void the payments first").
Recording and viewing payments is the Sales tier (Owner/Manager/Sales,
spec §5.1); voiding a payment is corrective (Owner/Manager only), like every
other undo.

**Outstanding balance (#89)** — per order: confirmed total − non-voided
payments; per customer: the same summed across their confirmed orders
(server-side sums, never client-aggregated pages). Shown on the order's
payments panel and the Customers page (admins).

**Expense (#87)** — a money-out record (spec §16, basic cut): date, category,
description, amount in minor units, optional flock link, optional note. The
currency snapshots from the account at creation and is never editable — a
later currency change must not re-denominate recorded spending. Corrections
edit in place under the Version token (mismatch → 409, the F16 water
pattern); there is no delete. Expenses are **admin-only end to end**, reads
included — the money/production split.

**Expense category (#87)** — per-farm buckets ("Feed", "Vet"), name unique
per farm case-insensitively (precheck + `lower(Name)` index, the grade
pattern). Deactivating hides a category from new expenses; recorded ones
keep it (grandfathering). List/sum endpoints report a server-side period
total — clients never sum pages.

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

**Auth rate limiting (#143)** — the anonymous auth endpoints are throttled per
client IP to blunt password spraying and token replay: **login** strictly
(default 10 / 15 min), **refresh** more loosely (default 60 / 15 min — it also
carries legitimate automatic session traffic, so it must not share login's
budget). Over the limit returns HTTP 429 with a `Retry-After`; the SPA surfaces
it as "Too many sign-in attempts" and never tears down an already-signed-in
session. The real client IP comes from the reverse proxy's `X-Forwarded-For`
via the framework's forwarded-headers handling (trusted-proxy networks are
configured; a direct caller can't spoof the header). Limits and trusted proxies
are configuration; in-process (single instance today — distributed limiting
would be a later concern if the API is ever scaled horizontally).

**Version (concurrency token)** — every mutable aggregate carries a `Version`
that each mutation bumps. Two concurrent edits: first save wins, second gets
a 409 and retries against fresh state. Append-only aggregates (bird
movements) don't need one.

**Operational day** — dates are farm-local calendar dates, resolved from the
farm's own timezone rather than UTC (#35). This is what "today" means for the
rules that turn on a date: whether an egg lot is still under **withdrawal
restriction**, which lots a sale may draw on, and whether a daily entry or bird
movement is dated in the future. All of them read the same boundary, so a lot
can never count as sellable on one screen and restricted on another.

It matters most around midnight. At 18:00 on July 15 in Los Angeles it is
already July 16 in UTC, so on a UTC boundary a lot restricted through the 15th —
eggs still inside a medication withholding period — would read as available a
day early. A farm ahead of UTC has the mirror problem: its legitimate today
looks like tomorrow. Recording a genuinely future day is refused outright; there
is no longer any day-either-side slack.

**Roles (#103, spec §5.1)** — five shipped: **Admin (owner)** does
everything including user management; **Manager** runs the farm — every
corrective, config, and money capability except managing users; **Worker**
(a user with no role) records the day's work, optionally narrowed to
**assigned flocks** (spec §5.3 — no assignments = account-wide, the first
assignment restricts); **Sales** handles customers, orders, and payments but
no production capture; **Read-only** sees stock, history, and reports only.
Vet/Consultant is deferred until a health module exists to gate, and
house-level scoping until houses are real entities. The SPA hides gated
controls; the API returns 403 with a problem body regardless. The role travels as a `role` claim in the
access token and is re-read at every token refresh.

**Users screen** — Owner-only user management (#103): create a user with
email, password, and one of the five roles, and manage worker flock
assignments (spec §5.3). Editing an existing user's role or password belongs
to a later slice.

**Export (manual backup) (#95)** — admin-only downloads of the account's
data as CSV files: one file per dataset, or a **full account export** — a
single zip with every dataset plus a manifest of row counts. CSVs are
RFC 4180 (UTF-8 with BOM), dates ISO-8601, money as raw minor units with
currency columns — a copy of what's stored, never a re-formatting. Cells
that a spreadsheet would run as a formula are prefixed so they render as
text. Scheduled backups belong to Phase 1.5 (spec §17.5).

**Dialog (#131)** — the popup that adding and correcting happen in. A
**New …** button sits beside the screen's title, and each row's **edit** or
**correct** link opens the same form seeded with that row's values; a
drill-down panel's actions (record a purchase, feed usage, a stock
correction, a bird movement, a customer payment) work the same way. The list
never shifts to make room for a form, and closing — **Cancel**, Escape, or a
click outside — leaves the data untouched. What a dialog submits is exactly
what the old inline form submitted: same fields, same validation, same
idempotency and version guards. Screens whose whole job is capture keep their
form on the page: **Daily entry**, **Water**, recording an expense, and
adding lines to a draft order.

**Confirmation (#135)** — the dialog a one-way action asks through before it
runs. Submitting a day, confirming or cancelling an order, depleting or
archiving a flock: each states what is about to happen and waits. **Cancel**
is what the keyboard lands on, so a stray Enter never takes the action, and
Escape or a click outside means no. The ones that **undo or retire** something
— void, cancel a draft, deplete, archive — colour their button red, the only
red fill in the app. Submitting a day and confirming an order are just as
irreversible but are the ordinary path through the week, so they stay
aubergine: a red button on the most routine action would say nothing.

**Void reason (#135)** — voiding a daily entry, a payment, or a confirmed
order asks in the same dialog, plus a required **Reason**. The reason is
stored with the void and shown wherever the voided record appears, so it is
the record of why. An empty reason is refused in place and what has been
typed stays put.
