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

The **adjust** dialog (#403) presents the same form: the same two numbered panes
in the same order, the same **Left to grade** chip, the same **steppers**, and
the same **Put all in…** remainder shortcut. So every term defined below for
capture means the same thing when correcting a day, and a correction is read the
way the day was recorded. Only what is genuinely the dialog's own — the reason
field, and the *previously adjusted* note — is particular to it.

What is literally shared is the reconciliation arithmetic and the chip and
remainder controls; each screen still renders its own form and owns its own
state and submission. So the two can drift, and keeping them saying the same
thing is a review obligation, not something the code enforces.

**Condition grade (#396)** — the Cracked and Dirty grades, fed by the Daily
Entry's own **counters** rather than by a hand-typed grade line. A farm may sell
them as separate stock (both are saleable on a new installation) or keep them as
losses by switching either off; **Discarded** is always a loss and is never a
condition grade.

Two things make the binding safe to rename around. The grade carries a fixed
**kind** (`Cracked` / `Dirty`) rather than being matched on its name, so renaming
"Cracked" to "Segunda" does not detach the counter from the grade. And the entry
**snapshots** which grade each counter resolved to at the moment it became
official — so turning a grade off later never rewrites a past day, and turning
one on never invents stock for a day recorded as a loss. Resolution requires the
grade to be **both active and saleable**; anything else records the condition as
a loss.

A condition grade can never be entered as a manual grade line — the server
refuses it, not just the screen. Its counter already produces a lot, so a manual
line naming it would produce a second lot for the same grade on the same day.

**Condition (Reports / History column, #396)** — the eggs a day produced that
became stock **without being hand-graded**: the condition counters above, counted
only where that entry resolved them to a grade. Deliberately shown beside
**Sellable** rather than added into it — Sellable is the hand-graded remainder
and the figure grading counts down to, so the two answer different questions and
are added together to get everything the day produced that can be sold.

**Left to grade (#134)** — grading counts **down** to zero rather than reporting
*graded n of m*. The figure beside the grades is how many sellable eggs are
still unaccounted for; it turns green when the day adds up exactly and red when
the grades overshoot the sellable count. Submitting is blocked unless it reads
**exactly zero** (#394) — short of sellable is refused just as much as over.

**Entry footer (#134)** — both saves sit in a bar pinned to the bottom of the
screen, along with save messages: anything below a pinned bar scrolls underneath
it and is never read. On a phone the two panes stack and scroll away, so the bar
also carries a compressed *n sellable · n left*; on a wider screen both panes are
already visible and repeating it would be noise.

**Steppers (#134)** — every count and grade field has **−** and **+** buttons
either side. Holding one repeats, widening its stride from 1× to 5× to 10× of
the counting unit, so a few hundred eggs takes about a second. Built for the
barn: the browser's own spinner is a ten-pixel target and disappears entirely
on touch.

**Stepper counting unit (#444)** — how much one tap of **−**/**+** counts by
on the Daily Entry screen (and History's adjust dialog): one egg by default,
or one of the farm's **packed units** (e.g. Tray = +30/−30) for a farm that
counts by the tray rather than the egg. Resolved as: the **user's own
preference** (Account screen, follows them across devices) if set, else the
**farm default** (Settings, admin-set), else Individual. The hold-to-repeat
stride multiplies this base, so a held Tray stepper accelerates in trays.
Only units with an **active** eggs-per-unit definition (the same §9.7 catalog
sales use) can be chosen; if a chosen unit is later deactivated, the stepper
quietly falls back to one egg rather than counting by a retired factor.
Typing stays plain numbers — only the guided control counts by units.

The unit is **visible at the point of touch**: when a pack unit is in force
the buttons themselves read **−30 / +30** instead of bare −/+ (and announce
"increase … by 30" to a screen reader), and a caption above the two panes
says which unit is counting and where a tap lands ("Counting by Tray — each
tap moves 30 eggs"). At one egg per tap neither appears — plain icons, no
caption — since "+1" would just restate the default.

A grade's **+** no longer stops once the day is fully graded (#443) — a farm
that counts the grades before adding them up needs to keep going past
whatever **1 Egg counts** currently says. Grading past the current sellable
figure raises that total to match instead of refusing the tap; it only ever
raises the total, never lowers it, so trimming the total on step 1 never
forces a grade back down. Typing already worked this way; the steppers now
match it. **Over** is still reachable — trim the total below what is already
graded — and it blocks both saves, not just Submit.

**Put all in… (#134)** — hands the entire remainder to one grade in a single
move, for the commonest last step of the day ("and the rest are Large"). Drag it
onto a grade, or tap it and pick one. Both routes exist deliberately: dragging
is unavailable on a phone and unreachable from a keyboard, so it is never the
only way. It disappears when nothing is left to place.

**Editing draft (#134)** — a badge beside the title when the day being captured
already has a saved **Draft**, so re-opening work in progress is distinguishable
from starting a fresh day. Only locked days carried a signal before.

**Daily entry lifecycle** (#69) — the arrow sketch this line used to carry
(`Draft → Submitted → Locked → ManagerAdjusted / Voided`) read as one straight
chain, which under-states the real graph in three ways: `ManagerAdjusted` is
re-enterable, `Void` is reachable from `Submitted`, `Locked` **or**
`ManagerAdjusted`, and a Draft cannot be voided at all. The drawn version is in
[`docs/architecture.md`](../../docs/architecture.md#daily-entry); the states
themselves:

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
  submitted or locked entry (reason required). The correction is made in
  the **Daily entry** two-step form itself (#403) — the same panes, chip
  and remainder shortcut described above — so an admin fixing a day works
  the way the day was captured, and the reconciliation feedback is the
  same feedback rather than a second interpretation of it. An adjustment
  has no draft state of its own, so it is held to the same **exact
  reconciliation** as submit (#394): the corrected grades must sum to
  exactly the corrected sellable count, with no partial-save escape
  hatch. The correction
  reconciles the entry's egg lots in the same transaction — grown, shrunk,
  added, or emptied — but **never below what a lot already sold**, and
  appends a compensating bird movement for any mortality change. The
  replaced values are kept as an audit snapshot on the entry until the audit
  log lands. Adjusting again is allowed; each adjust snapshots what it
  replaced.
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
survived the day. This is the only rule a **Draft** enforces — a draft may be
graded partially, or not at all, and still be saved. Since #443 the capture
screen keeps the two sides from colliding on its own — grading past the
current total raises the total to match — so this cap is normally satisfied
by construction rather than by refusing input. It still applies: trimming the
total below an already-graded sum reaches it, and both saves are blocked (the
same **Left to grade** chip turns **over**) until the numbers agree again.

**Grade reconciliation (#394)** — **Submit**, and **saving an adjustment**
(which has no draft state of its own to leave incomplete), both go further
than the sellable cap above: the grade lines must sum to *exactly*
`total − cracked − dirty − discarded`, not merely fit within it. Zero
sellable eggs validly reconciles to zero grade lines — a day where every
egg was lost needs no grading to submit. Short of sellable and over
sellable are refused the same way, at both the domain and the API layer,
so a direct API caller cannot bypass it any more than the SPA can. This
closes the gap where an ungraded, no-loss day could submit cleanly and
silently produce zero stock for real production.

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
corrections, and `Discard` / `InternalUse` / `Reconciliation` for stock
write-offs (see below). Written in the same transaction as the lot change, so
the cached `QuantityAvailable` always equals the sum of the lot's movements —
the tech-spec rule that cached balances must be rebuildable from ledgers.

**Stock write-off (#406)** — an Owner/Manager correction that removes lost
eggs from a specific lot (breakage, spoilage, theft → `Discard`; farm
consumption → `InternalUse`) or applies a recount (`Reconciliation`, which
alone may also add eggs back), with a required reason. It moves only the
lot's available quantity — the daily entry's production figures and hen-day %
are never restated, which is what separates it from a daily-entry adjustment.
Available is floored at zero — a write-off can never consume eggs already
sold — and a positive recount is capped at the lot's cumulative write-off
total (never at raw production: eggs allocated to a confirmed order must not
read as headroom, or they'd be sold twice). A recount beyond that means
production or a sale is wrong, which have their own paths. Withdrawal
restriction does not block a write-off — restricted eggs spoil too, and
removing stock is the safe direction.

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

The unit is visible at the point of entry (#445): a sales line's quantity
counts *units, not eggs*, so the add-line form names the unit in the quantity
label ("Quantity (tray)"), previews the resulting egg count live while typing
("= 60 eggs"), and shows the unit size on the product picker option ("(30
eggs/tray)") — typing the egg total where the unit count belongs (a 30×
oversale) is visible before the line is added, and the eggs column tracks an
inline quantity edit the same way.

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

Recorded on the **Feed page** (#446) — its own capture form plus the feed
history (filterable, paginated, with per-row estimated cost); an item's
Inventory panel deep-links there with that item preselected. Each feed and
water record also carries a **daily-entry link**: the non-voided daily entry
that existed for the same flock's (farm, house, flock, date) *at the moment
of recording*, or nothing if the day's entry didn't exist yet. The link is
best-effort provenance — it is never backfilled when the entry arrives
later, never changed by a water correction, and a later void of the entry
does not clear it. Flock + date remains the authoritative way the app joins
feed/water to a day (the Daily Entry page's own summary strip joins that
way), so an empty link never hides a record.

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
is selling units and always a whole number — the add/edit-line controls
reject a fractional value before sending, and a direct API request gets a
stable validation response rather than a raw JSON-binding error (#398) —
while `quantity_base = quantity × factor` is individual eggs; allocation and
the stock guard run on `quantity_base`. Re-pointing a product's grade or
redefining a packed unit only affects future lines, never recorded ones.
Price is per selling unit (decimal money, stored as integer minor units),
prefilled from the product's default and editable per line.

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

**Every event names an actor, and nothing can write one that does not (#500).**
For a request that is the signed-in person. For the offline operator verbs,
which have no human by design, it is one of five explicit **system actors** —
`(bootstrap-admin)` for the default farm's first Owner, `(break-glass)` for a
`recover-admin` password reset, `(suspend-account)` and `(reactivate-account)`
for farm lifecycle changes, and `(provision-account)` for a new farm and its
first Owner — chosen deliberately rather than defaulted. An
event whose actor was never resolved is refused outright, so a record can never
be filed with no author at all. Sample data is held to the same rule: a **demo**
farm's records are signed by its Owner, and a **simulation** farm's by the member
of staff who would really have made them — workers record the eggs, sales staff
book the orders.

**Record history (#494)** — the "Created by … / Last changed by …" column on
the Flocks, Egg grades, Daily entry history, Sales and Expenses tables. Not a
stored field: it is **derived from the audit log**. Creation is the record's
`*.Create` event — identified by the action, never by being first on the trail,
because two events can share an instant and their order is then unknowable.
Creation itself became an audited action for these five in the same change
(`Flock.Create` and siblings) — before that only corrections were on the trail.

The last change is the latest event that is neither that creation **nor a
promotion by the person who created it**. A *promotion* is the action that turns
a draft into the official record — `DailyEntry.Submit` and `SalesOrder.Confirm`,
the two moments that mint or allocate stock. That exception is what makes the
common case read correctly: saving a daily entry and submitting it are two events
but one act, so a farmhand who writes the day's numbers and makes them official —
changing nothing in between — is shown as the creator with no change against
them. When somebody *else* submits the draft, that submit is a real change and
both people are named, which is the accountability the submit step exists for.

Suppressing the promoter's *name* must not lose the promotion's *time*, so the
column also carries **when** the record became official — "Submitted …" on a
daily entry, "Confirmed …" on a sales order — shown whoever did it. That instant
is when stock is minted or allocated, and it lives nowhere else on the record's
own page: a daily entry stores no submission timestamp of its own, only
`LockedAtUtc`. Blank for a draft awaiting promotion, and absent entirely on
flocks, egg grades and expenses, which have no promotion step.

The exception covers **drafting** — the creator's own edits to their own draft,
and the promotion that ends it — and nothing else. Correcting a locked entry
always shows, including when the corrector created it. So does **cancelling** a
draft order: `SalesOrder.Cancel` kills the record rather than making it official,
so cancelling your own order stays a reportable change. It is keyed on the action
*and* the actor together, which is why the same draft edit is hidden for the
person who created the record and shown for anybody else.

Editing a draft **is** recorded, even though it alters no stock, because it is
the only thing binding a person to the numbers. Without it, someone who rewrites
a colleague's draft before it is submitted leaves no trace and the submitter is
credited with their work. The rule above is what keeps that quiet in the ordinary
case: your own edits to your own draft are part of writing it, so they are hidden
along with your own submit, and only a **different** person's edit surfaces as a
change.

That hiding stops the moment the draft stops being yours alone. **Once somebody
else has edited it, your own later edits are shown too** — because by then they
are the answer to "whose numbers are these". Hiding them named the colleague
whose work you overwrote and dated the record to an edit that no longer exists
in it, while the numbers that went into stock were yours. Your own *promotion*
stays hidden even here: it has its own "Submitted …" line, and repeating it as
the last change would say that making a record official changed it.

Two further consequences follow from being derived rather than stored. A record
created **before #494** has no creation event and is **never backfilled**, so it
shows no "created by" line — but that half is independent of the rest: if it has
since been changed, that change still shows, with the created line simply absent.
The column is entirely blank only for a record with nothing on the trail at all.
And who can see it is exactly who could already read the record — it adds
no gate of its own, so a worker reading their own daily entry also sees which
manager corrected it. The actor is shown as the plain **email snapshot** the
audit log already keeps, deliberately not a live-joined name: a later rename or
disable must not rewrite what old history displays. Distinct from the audit
log's own admin-only viewer at /audit, which is the full cross-cutting trail —
this is the one-line summary on the record's own page.

**Entity-scoped audit history (#493)** — the bridge between the two. An
"Audit history" link on a record's own screen (the same Flocks, Egg grades,
Daily entry history, Sales and Expenses tables #494's summary covers) opens
the admin-only /audit viewer pre-filtered to that one record (`?entityId=<id>`),
reusing the `entityId` filter the endpoint already supported server-side but
that nothing called until this feature. Not a new surface: same viewer, same
admin gate, same action-type filter — just scoped. Distinct from the
record-history summary above in the same way the summary itself is distinct
from the full log: the summary is two points (created, last changed), the
scoped view is every event on the record, in order.

Egg lots get the **narrower** "Adjustment history" link instead, deliberately
labeled differently. The only audit action ever written against an egg lot's
own entity id is a manual write-off or recount (`EggLot.Movement`) — creation
is recorded against the daily entry that produced the lot, allocation and
restoration against the sales order, never against the lot itself. Labeling
this link "Audit history" like the other five would promise a full lifecycle
the data can't deliver: a normal, never-adjusted lot shows nothing at all
under that scope. "Adjustment history" says exactly what it shows.

**Record-type filter (#520)** — a second dropdown on the /audit viewer,
ahead of the existing action filter. Picking a record type (Flock, Sales
order, User, …) does not query anything itself — it narrows the action
dropdown's option list to only the actions actually recorded against that
type, so an admin who knows *what kind of record* they're after isn't
scanning one flat list of every action the farm can log to find it. The
mapping from action to type is read off the server's own audit-write call
sites, not the action code's own "Entity.Verb" prefix — the four
`Account.Set/RemoveLogo/Banner` actions are prefixed `Account` but recorded
against `FarmLogo`, a row shared by both the logo and the banner.

**Hen-day % (#91)** — eggs collected ÷ hen-days × 100 (spec §19.3). A
hen-day is one bird alive for one day; the day's bird count comes from the
bird ledger (placements + movements). The production report shows it per
day and for the whole period (period eggs ÷ period hen-days — not an
average of daily percentages).

**Production report (#91)** — per-day official production over a range
(Draft entries aren't submitted, Voided ones vacated their day — neither
counts): eggs, losses, sellable, deaths, hen-day %, period totals, grade
breakdown. Open to workers — it is their own recorded work.

**Report query bounding + concurrency limit (#311)** — every report (production,
sales, expenses, profit) is bounded to a validated range (default the last 7
days, capped at 366 — see Production report above) and its aggregation runs in
SQL rather than loading the account's full history into memory, so cost tracks
the requested range and flock count, not the farm's lifetime of data. On top of
that, concurrently in-flight report requests are capped **per account** (a
small default, e.g. 4 at once): a request over the cap gets HTTP 429 with a
`Retry-After` header instead of queueing or running unbounded — a documented,
retryable "try again shortly," never a silent hang or a degraded shared
service. One account's usage never affects another's — each account has its
own bucket, mirroring the per-IP auth rate limiting (#143) above but keyed by
account instead of client address, since a report is behind auth.

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

**Account** — the tenant, presented to users as a farm. Multiple accounts can
coexist; the farm code selects the tenant at sign-in, and the same email address
can belong to users in several farms. Every tenant-owned row carries
`AccountId`; the API enforces isolation with EF global query filters.

**Farm code (account slug, #531)** — the per-farm login identifier: a short,
stable, URL-safe slug (`Account.Slug`) for an account — lowercase letters,
digits and hyphens, 3–32 characters, no leading or trailing hyphen. Unlike the
account's internal id (a GUID), it is meant to be typed and read aloud. It is
chosen once and **immutable** — a provisioning typo has no in-app fix this
phase — and a handful of words are reserved (`api`, `admin`, `www`, `health`,
`app`, `login`, `auth`, and similar). The default farm's code is
`default-farm`. Operators discover the codes with the `list-accounts` command.
The farm code is the way to disambiguate login across farms (#532): the sign-in
form requires it before the email, because one email address can now exist in
several farms and only the farm code says which one is meant, and a wrong or
unrecognised one is refused with its own message. (Written earlier, before
#532 shipped: it was recorded and discoverable but not yet used at sign-in, and
there was no SPA surface for it.)

On the SPA sign-in page the field is prefilled in two ways. A `?farm=<slug>`
link prefills the field with a validated slug (an invalid value is ignored, not
truncated, and no error is shown). And the page remembers farm codes that were
used to sign in successfully on this device and offers them as a picker: a
single remembered code prefills the field, and every remembered code — one or
several — renders a picker entry. Each entry is individually **revocable**
(#587): its Forget control, behind a destructive confirmation, removes that one
code from the device-local roster without clearing language, theme, or any
other per-device preference, and without touching any other farm's session.
Accepted disclosure: on a shared device the cached list is a durable roster of
which farms that browser profile uses — now revocable entry by entry, tracked
in #587, with the ADR revision owned by #537.

**Login email (#357)** — the address used with a farm code to sign in. An Owner
can replace it immediately from the Users screen; the new address becomes the
next login, the old address stops working, the user's open sessions end, and no
confirmation email is sent. Changing an Owner's own address is allowed only
while another active Owner remains on the farm.

**Farm provisioning (#533)** — the offline `provision-account` operator command
creates a new account, its ten canonical egg grades, its six packed-unit
conversions, and its first Owner as one transaction. The Owner receives a
generated one-time password and must replace it at first sign-in. A new farm
starts in UTC; after that password change, the Owner selects the farm's IANA
timezone in Settings. The command does not migrate the schema and is intended
to run with the ordinary DML-only runtime database role after the migration
job. A farm code is immutable, so the command echoes its normalized value
before writing and the database's unique index is the final authority when two
operators race for the same code.

**Account status — active / suspended (#531/#532/#534)** — an account is
*active* by default. **Suspending** it takes the farm offline; **reactivating**
brings it back. Suspension is **immediate**: a suspended farm's users are
refused at sign-in *and* on their very next authenticated request, not merely
when their token expires, and every refresh session is revoked. Signing out
still works. Nothing is deleted, so reactivation restores the farm exactly —
except that sessions issued before the suspension stay dead, so everyone signs
in again. Operators run `suspend-account --slug <farm-code> [--reason <text>]`
and `reactivate-account --slug <farm-code> [--reason <text>]` at a shell on the
deployment. Each writes an audit row **when it actually changes the farm's
state**, so re-running either is safe and leaves one row per real transition.

**Farm / House** — physical hierarchy. Present as ids from day one, but real
Farm/House management arrives with later phases; each account runs on one
seeded farm and house. The farm's *settings* are already real (see below) —
they live on the **account** row, which is why an account is presented to users
as "a farm".

This reliably confuses readers, so state it plainly: **`FarmId` and `HouseId`
are per-account stand-ins for a sub-entity that does not exist yet, and they are
NOT the tenant.** The tenant is `AccountId`. Multi-farm tenancy (#530) added
*accounts*, not Farm/House management. Every unique index **that is scoped to a
farm** is already `(AccountId, FarmId, …)`-prefixed, so the stand-ins stay
correct as they are. Not every unique index is — the account slug is globally
unique by design, and several others are keyed by their own parent rather than
by farm.

**Farm settings (#123, spec §4.5)** — the farm's own name plus the four things
that decide how it reads: **timezone**, **locale**, **currency**, and **unit
system**, with optional **first day of week** and date/time format overrides.
Owner and Manager can edit them; everyone reads them, because formatting money,
dates and numbers is not a permission. They live on one screen — **Setup → Farm
settings** — which is also where the logo is uploaded and cleared.

What each one *does today* is not yet what it will do. The **timezone** is fully
wired: it decides the operational day, and every capture screen's date field
follows it the moment it is saved. **Locale**, **unit system** and the
date/time **format overrides** are stored and validated, and nothing renders
through them yet — money still prints as `12.34 USD` and dates as ISO. They are
recorded now so the display work (#45, the i18n infrastructure) has settings to
read rather than a migration to run; the settings screen says as much rather
than promising an effect the app does not yet have.

Each one has teeth. The **timezone** is the farm's **operational day** — change
it and every date rule moves with it the same minute, so a timezone that the
system cannot resolve is refused outright rather than stored (the clock refuses
to guess a date, so an unusable zone must never reach the column). The
**locale** must name a real region — `en-US`, not `en` — because a region is
what carries number and date conventions. Saving uses the same **version
(concurrency token)** as everything else: whoever saves second against a stale
copy gets a 409 and reloads.

**Farm logo (#123)** — an image shown as branding in the app chrome, falling
back to Cluckwork's own branding when none is set. Owner and Manager can upload
or clear it; everyone sees it. **PNG, JPEG or WebP, up to a configurable cap (2 MB by default) and 4096
pixels a side, and it must be a still image.** SVG is refused: it is a document that
can carry script, and this image is rendered back to every user of the farm.
Animated PNG and animated WebP are refused too — an animated frame can hide
data inside itself where the check that cleans the rest of the file never
looks.

The chrome renders it in a short row — a fixed height (~24px, about a line of
text) with the width following the image's own aspect ratio, bounded so a very
wide image cannot crowd the farm name out of the sidebar. So a simple mark and a
**wide wordmark** both read correctly; a wordmark uses the horizontal space
instead of being squashed into a square. What still does not survive that size
is a detailed illustration or a busy scene, which shrinks to something
unreadable — that belongs on the **farm banner**, below, a separate image
entirely.

This was not always so (#179). The slot was originally a fixed 26×26 **square**,
and because it fits the whole image (`object-fit: contain`) a wide logo kept its
aspect ratio and gave up its height, rendering as a sliver — `contain` prevents
cropping, but on a square slot that is exactly what collapses a wordmark. The
guidance then was "upload a square mark", which was a workaround for the slot
rather than advice about logos. Two changes retired it: the banner gave detailed
art a home of its own, and the slot itself became height-driven.

**Farm banner (#179)** — a second, independent image: a wide/hero picture shown
full-size on a **post-login splash**, once per login, rather than in the short
sidebar row the logo occupies. A farm can have a logo, a banner, both,
or neither — setting or clearing one never touches the other. Same upload rules
as the logo (PNG/JPEG/WebP, a still image, no SVG, dimensions and metadata
handled the same way — see below), but its own larger size cap (5 MB by default,
since a detailed hero image is typically heavier than a small sidebar mark) and its own
Owner/Manager-only upload and everyone-sees-it read, same as the logo. The
splash is skipped entirely when no banner is set — it is never shown empty, and
never shown on the pre-login screen (that screen has no farm to show a banner
for yet).

What gets stored is never quite the file that was uploaded. The image is taken
apart and rebuilt, which drops two things on purpose: **embedded metadata** — a
photo taken on a phone carries the coordinates of where it was taken, which for
a farm is its address — and **anything appended after the image ends**, which is
the usual way something that is not an image hides inside one. The format is
decided by reading the file, not by trusting its name or what the browser called
it. A picture far larger than it looks is refused too: dimensions come from the
image's own header, so a small file claiming to be 30000 pixels across is turned
away before it can lock up the browser of everyone who loads the page.

**Farm palette** — The accent colour used across the app for everyone on a
farm, chosen by an admin in Farm settings from a curated set (Aubergine,
Forest, Slate, Terracotta). It is farm-wide and applies to every role. Distinct
from **night mode**, which each person sets for themselves on each device: the
two are independent, and choosing a farm palette never changes anyone's
light/night preference. The set is curated rather than free-form because every
palette ships a contrast-checked light and dark pair; an arbitrary colour
cannot be held to that bar.

Since #586 the palette is remembered per farm on each device, so a farm's own colour can appear on the
sign-in screen before anyone signs in — when the sign-in link names that farm (`?farm=<code>`), or when
the device remembers exactly one farm. A device that remembers several farms shows the default until
sign-in, because at that point the app does not yet know which farm it is signing in to. Forgetting a
farm removes its remembered colour along with its code.

**Farm locale** — the farm-wide setting (part of **farm settings**, spec §4.5)
controlling how numbers, dates, and currency are formatted and displayed. Must
name a real region code (e.g. `en-US`, not `en`) because regions carry number
and date conventions. Independent of **UI language**: a user reading the
interface in English can still see figures formatted for their farm's chosen
locale.

**Currency change rule (spec §4.6)** — the farm currency can only be changed
while the farm has **written down no amount at all** in the current one: no
sales orders, no payments, no expenses, no priced product, and no feed money
(a purchase, a stock lot, or an item's default cost). Every one of those keeps
the currency it was recorded in and keeps it forever, so changing the farm
currency afterwards would leave the books reading in two denominations at once
— and a catalog price is worse than stale, because an order line that takes it
re-labels the raw number with the *order's* currency: $12.34 sold as ¥1,234.
The spec names the first three; the rest follow the same rule for the same
reason. The settings screen
shows the field locked with the reason before it is tried; the API refuses it
either way, and refuses the whole save, not just that field. A farm that truly
changes operating currency gets a new farm record — history is never
re-denominated. When the change *is* allowed, the currency's **symbol** and
**minor unit** are re-derived from the standard: `JPY` has no decimals, `KWD`
has three, an unrecognized code falls back to showing the code itself and two
decimals.

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

**Account lockout (#128)** — a complement to the per-IP rate limit, working per
**account**: repeated failed logins (default 5) lock that one account for a
cool-off window (15 min), after which even the correct password is refused. The
reply is the SAME generic "Invalid email or password" (and PBKDF2 is still paid)
as a wrong password or an unknown email, so neither account existence nor
lockout state is revealed by the response. A successful login clears the
counter, and each failure is counted even under parallel attempts. Where rate
limiting blunts a spray across many accounts from one address, lockout blunts a
focused guess at one account from many addresses.

**Session tokens (#145)** — the short-lived **access token** (15 min) lives only
in the SPA's JavaScript memory and is sent as an `Authorization: Bearer` header;
it is never written to `localStorage`/`sessionStorage`, so an XSS payload has no
durable credential to steal and the 15-minute lifetime bounds any exposure. The
durable **refresh token** is an `HttpOnly; Secure; SameSite=Strict` cookie
path-scoped to `/api/v1/auth` — the browser attaches it automatically and JS
cannot read it. Its name includes the farm account ID (#532), so one browser can
hold independent sessions for several farms without one farm's login, refresh,
password change, or logout overwriting another's cookie. The SPA keeps the
non-secret farm ID in per-tab `sessionStorage`, while the access token remains
memory-only, so a reload names and silently restores that tab's farm even when
the browser holds several farm cookies. A fresh tab with several cookies and no
remembered farm still makes the server refuse to guess
(`Auth.FarmSelectionRequired`) and sends the user to login to choose a farm; it
rotates or clears none of them. Explicit logout removes the tab binding, and
closing the tab discards it with the rest of `sessionStorage`. An expired/absent
cookie lands cleanly on login.
CSRF is covered by SameSite=Strict plus a custom header (`X-Cluckwork-Auth`) that
a cross-site request cannot set. Rotation + theft-detection (single-use, revoke
the whole family on replay) are unchanged — this moved the storage, not the
hygiene. Deploying #145 forces one re-login (the old localStorage token is
purged on first load). Tabs on the same farm still share that farm's cookie, so
two same-farm tabs refreshing at once would each present the same value and the
second could trip theft-detection, logging both out; refresh is therefore
**serialised across tabs** via the browser Web Locks API (#169) so only one tab
refreshes at a time and the next presents the freshly-rotated cookie. Server
theft-detection stays strict; browsers without the API fall back to per-tab
coordination only. As the server-side safety net for the case a lock can't cover
(a tab dying between sending a refresh and receiving the rotated cookie),
reuse-detection carries a short **idempotency grace** (#176): a token rotated in
the last few seconds whose replacement is still the live tip is treated as a
benign retry — the caller gets a fresh token instead of the family being revoked.
The grace is deliberately tiny (default 10s) vs the ~15-min refresh cadence, and
**bounded to a single hop** (the link revoked by a grace-advance can't itself be
graced), so a stolen token can't be leap-frogged down the chain and a genuinely
replayed/stale token is still caught and revokes the whole family. Consuming a
token is an atomic compare-and-swap (a per-token concurrency stamp), so concurrent
replays can never fork one token into two live sessions.

**Superseded-flight cookie revocation (#393)** — a refresh (or sign-in, or
password change) that goes stale mid-flight — superseded by a newer sign-in
before its own response lands — still rotates its farm's refresh cookie the
instant the browser receives that response, before the app's own bookkeeping
ever runs. The stale flight's cookie is therefore always revoked. Which of two
responses for the **same farm** the browser kept is real network timing, so a
same-farm account switch during an in-flight refresh can still surface as an
unexpected "please sign in again." A different farm's cookie is a different
name and cannot be touched by that race (#532). No work is lost; see the Help
page's "Signing in" section.

**Credential epoch (#364)** — a monotonically increasing per-user number carried
in every access token and stamped onto every refresh token. A request is valid
only when its epoch matches the current user record, so an administrative
credential reset can invalidate access tokens immediately without a per-request
revocation list. The epoch readers ship before the mutations that advance it;
deploys must drain older replicas before enabling those mutations.

**Disabled user (#356)** — an Owner-only Users-screen action that revokes a
colleague's access without deleting them. A disabled user cannot sign in,
cannot refresh an existing session, and cannot obtain or spend a **step-up
grant**; every one of their live sessions stops working on its very next
request, not once the access token's ~15-minute lifetime happens to run out —
the disable bumps the target's **credential epoch** (see above), rotates
their security stamp, and revokes every refresh token, mirroring the side
effects a role change applies. Re-enabling is reversible but deliberately
**not symmetric on the credential epoch**: it also rotates the security
stamp (so a stale concurrent password-reset write cannot silently restore
the disabled flag), but it does NOT roll the credential epoch back, so every
credential issued before the disable stays permanently dead and the user
signs back in fresh; nothing minted before the disable is ever revived. The
account's last active Owner cannot be disabled
(refused inside the same account-locked transaction that guards a demotion),
and nobody can disable themselves (refused at the endpoint before validation
even runs). This is not deletion: a disabled user's row, their audit trail,
and every record elsewhere that names them (created-by trails, refresh-token
history, orders they raised) are untouched; hard delete of a user account is
out of scope here (personal-data erasure is #272). Break-glass recovery
**refuses** a disabled target rather than resetting its password — a reset
would not restore access, because a disabled user is turned away before the
password is ever checked; re-enable them first.

**Version (concurrency token)** — every mutable aggregate carries a `Version`
that each mutation bumps. Two concurrent edits: first save wins, second gets
a 409 and retries against fresh state. Append-only aggregates (bird
movements) don't need one.

**Operational day** — dates are farm-local calendar dates, resolved from the
farm's own timezone rather than UTC (#35, #155). This is what "today" means for
every rule that turns on a date: whether an egg lot is still under **withdrawal
restriction** and which lots a sale may draw on; whether a daily entry, bird
movement, feed or water usage, purchase, stock correction, expense, payment or
flock placement is dated in the future; the day a flock's **deplete** or
**archive** is stamped with; and the default window and future guard on
**reports**. They all read one boundary, so a lot can never count as sellable on
one screen and restricted on another, and "in the future" cannot mean different
things on two screens of the same farm.

It matters most around midnight. At 18:00 on July 15 in Los Angeles it is
already July 16 in UTC, so on a UTC boundary a lot restricted through the 15th —
eggs still inside a medication withholding period — would read as available a
day early. A farm ahead of UTC has the mirror problem: its legitimate today
looks like tomorrow. Recording a genuinely future day is refused outright; there
is no longer any day-either-side slack.

The date fields agree with it (#123). Every picker that records *when something
happened* — daily entry, flock placement and bird movements, water, feed
purchase and usage, expenses, sales orders and payments, and the `to` end of a
report range — opens on the farm's today and refuses to go past it, the farm's
and not the one on the device in your hand. A phone travelling ahead of the farm
used to offer a day the save would then refuse, and a phone behind it hid a day
that was perfectly legal; both are gone.

Three date fields are deliberately *not* capped, because a future date is the
point of them: a feed lot's **expiry**, and the range filters on History and
Water. Change the farm's timezone and every capped picker follows on the next
screen that renders.

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
email, password, an optional **display name**, and one of the five roles, and
manage worker flock assignments (spec §5.3). A user's name can be set at
creation and later changed from the row's **edit** action (#163; blank clears
it back to "—"). The row's **password** action sets a new password without
knowing the current one (#165) — the forgot-password path, since there is no
email reset. The row's **role** action changes an existing user's role —
promote or demote among the five roles (#355). It **refuses self-targeting**
(ask another Owner) and refuses demoting the account's **last Owner** (a farm
cannot lock itself out of user administration). Every role change requires a
**step-up grant** (see below), whether it promotes, demotes, or apparently
resubmits the current role. Any actual change is audited (`User.RoleChanged`,
old to new) and, like a password reset,
bumps the target's **credential epoch** and revokes their sessions — a demoted
user's already-issued access token stops working on its very next request, not
merely once its ~15-minute lifetime runs out.

**Password change (#165)** — two paths with different rules. An Owner sets any
*other* user's password from the Users screen without the current one; any
signed-in user changes their OWN from the **Account** screen by proving the
current one. The Owner path **refuses self-targeting**: skipping the
current-password proof for your own account would turn a stolen access token
into a permanent takeover, so an Owner changes their own password like everyone
else. One Owner *can* reset a co-Owner's password (all Owners are equivalent);
every administrative reset requires a **step-up grant** (see below), regardless
of the target's role. It is audited (`User.PasswordSet`).
Both revoke every refresh token for that user, so other devices are signed out
— the self-service path hands the device that made the change a fresh pair so it
stays signed in. Since #364, both paths also bump the user's **credential
epoch** (see above) in that same transaction, and every authenticated request
is checked against it — so an already-issued access token is rejected on its
very next request, not merely bounded by the ~15-min access-token lifetime.

**Step-up authentication (#308, #360)** — a fresh proof of identity required,
on top of a normal valid Owner access token, before six categories of action:
**creating any user**, **resetting any user's password**, **changing any user's
role** (#355), **changing a login email**, **disabling a user**, or
**re-enabling a user** (#356). The first three are unconditional: roles grant
different, non-ordered capabilities, and every new or replaced authenticator
can outlive a stolen-but-still-valid Owner access token (good for ~15 min —
merely holding it bumps no credential epoch, see **Credential epoch** above).
Disable/enable and login-email changes likewise alter durable access. Editing
a display name and assigning or unassigning flocks remain **ungated**. Flock
scope can narrow or widen a Worker's durable authorization — including
restoring farm-wide Worker scope when the last assignment is removed — and is
an explicit follow-up boundary rather than being declared safe by this change.
The mechanism is **current-password re-confirmation**: the Users screen shows
an inline "your current password" field in each gated action's dialog (never a
separate popup), which the SPA exchanges for a short-lived (5 minutes by
default), single-use, account-and-user-bound **step-up grant** via
`POST /auth/step-up` and attaches to the one follow-up request. The grant is a
JWT carrying a DIFFERENT audience than the normal access token, so it cannot
be used as a Bearer token anywhere else; it is invalidated by a security-stamp
change (any password change/reset for that user) and by that user logging out,
and a used or expired grant is refused. Every rejection reason (missing,
expired, replayed, wrong actor, wrong account, stamp-revoked, or
logout-revoked) returns the identical non-enumerating denial, so a caller
cannot tell which one applies. The entered password itself is never stored —
held only in the dialog's own field and cleared the instant it is sent.
Trusted offline provisioning, seeding, and `recover-admin` commands use their
separate one-shot trust boundary and never select a browser-visible bypass.
TOTP/WebAuthn step-up is a deferred follow-up (#320).

**First-run admin provisioning (#283)** — how a fresh deploy gets its first
Owner without ever shipping a repo-known credential. The default account, the
four assignable roles, and the default egg grades are **static reference
data**: they ship inside the EF migrations themselves (no runtime seeder, no
`Seed:*` config — a migrated database already has them). The first admin does
**not**: an operator runs the offline `bootstrap-admin --email <address>`
command once, which generates a random password, creates the Owner with it,
and writes the password to **stdout only** — never the application logger or
the OTLP pipeline — and prints the **farm code** alongside it, which #532 made
a required sign-in input. A host's stdout collector (docker logs, journald, a
platform log pipeline) may still capture it, so that output must be treated
as sensitive while the password is valid. Re-running the command against an
already-provisioned account is a safe no-op (no second Owner, no password
reprinted). Because a migrated-but-unprovisioned **default account** has **no
administrator to sign in as**, the **login screen says so**: attempting to sign in while the
default account has no Owner answers with a short notice explaining that no
**administrator** account exists yet and pointing at whoever administers the
server, instead of the usual "invalid email or password". For the operator this
exists to help — who holds no credentials at all yet — that generic denial
describes a problem with their typing that they do not have. It disappears
for good once the default account has an Owner. The notice deliberately publishes **no
command and no deployment detail** — a page reachable by anyone is the wrong
place to describe how the server is run, and the setup steps belong in the
README. It is reported on the **failed sign-in itself**, not by a status check
the page polls, so only someone actually attempting to sign in is told anything.
It does not **enumerate**: the condition is a property of the **default
account** and never of the address that was typed, so no attempt reveals
anything about any particular account, and once that account has an Owner the
response is byte-identical to the ordinary non-enumerating denial. It does
**disclose one fact** — that the default account has no Owner — and that scope
is exact in both directions: it can be true while ordinary non-Owner accounts
exist and are protected (the predicate is the absence of an *Owner*, and the
seeders create Workers and Managers without one), and it can be true while an
Owner exists under a *different* account, since the predicate is scoped to the
default account rather than to any Owner anywhere. Accepted deliberately: the fact is not a credential and grants no access;
on a genuinely fresh install — no users at all — it is already inferable by
anyone who can reach the form; and the alternative is leaving an operator
stranded at a form no credential of theirs can satisfy. This is a **separate mechanism** from break-glass recovery below (a pre-auth, one-shot
setup secret vs. an offline recovery for a locked-out existing account) and
from the browser step-up re-confirmation a signed-in Owner uses for sensitive
actions (#308) — three distinct credential types, audiences, and lifetimes,
never conflated.

**Must-change-password gate (#283)** — the flag (`ApplicationUser
.MustChangePassword`) that forces the printed one-time password to actually
get replaced instead of sitting valid indefinitely. Set by `bootstrap-admin`
and `provision-account` on the Owner each command creates. While set, the access
token carries
a matching claim, the SPA shows a **"Set your password"** screen in place of
the app (any route), and the API refuses every other endpoint with 403 except
signing out and the change-password call itself. Submitting the printed
password as the "current" one and a new password clears the flag — the same
`/auth/change-password` endpoint the Account screen's regular self-service
change already uses. Resetting a user's password through any other path (an
Owner's Users-screen reset, break-glass) also clears a pending flag, so a
locked-out first-run admin is never left stuck.

**Break-glass reset (#265)** — the recovery path for the one case that
**self-service password change** and **first-run admin provisioning** leave
uncovered: a **sole Owner** who has lost their password. There is no email
reset, self-service needs the current password, and an Owner cannot reset their
own account — so without this the only recourse is direct database surgery. An
operator with server access runs the offline `recover-admin` command, which
resets the password to a freshly generated temporary one, **clears any lockout**,
rotates the security stamp, and revokes every session, printing the temporary
password and the farm code to stdout. It is audited as
`User.BreakGlassReset` (distinct from `User.PasswordSet`, and recording the
reason + the host it ran from) so the reset is conspicuous in the audit log. The
temporary password must be changed on first login.

**UI language** — a per-user preference controlling the language the interface
renders in. Stored on `ApplicationUser.Language` as a BCP-47 primary language
subtag (e.g. `en`) in lowercase; `null` means follow the app's default. Three
packs ship today — English (`en`), Spanish/Español (`es`), and Tagalog (`tl`,
both machine-drafted, pending native-speaker review) — selectable per-user
from Account → Preferences. English is the fallback for any string a pack
hasn't translated yet: the string sweep (#182) is ongoing, so screens not yet
externalized to the catalog still render in English regardless of the chosen
language. Set via `PUT /api/v1/me/language`; read via `GET /api/v1/me`.
Independent of **farm locale**, which controls number/date/currency
formatting.

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
click outside — records nothing and discards what was typed: reopening starts
from a clean form, never a half-finished one (#314). What a dialog submits is exactly
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

**Install to home screen (#142)** — Cluckwork can be installed from the browser
onto a phone or tablet, where it gets its own icon and opens in its own window
with no browser chrome. It is the same app either way, not a separate download:
there is nothing to update from a store, and it still needs a connection to do
anything — installing does **not** make the app work offline. Offline capture is
a later, deliberate piece of work.

Installing is only offered on a **secure (https) connection**. On a plain-http
address the browser simply won't show the option; the app itself works exactly
as before.

**New version is ready (#142)** — after a deploy, an installed app notices the
new version in the background and asks before switching, rather than reloading
underneath whatever is being typed. Accepting reloads onto the new version; the
prompt reappears next time if it is dismissed. Nothing is lost by leaving it —
the running app keeps working until the switch is accepted.
