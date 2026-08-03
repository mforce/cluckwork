# Daily Entry Reconciliation and Saleable Quality Eggs

Tracks #394 and #396. Implementation must follow the non-CI writer rule in
PR #395.

## Goal

Prevent an official Daily Entry from losing production, while allowing a farm to
sell Cracked and Dirty eggs as separate, optionally discounted stock.

## Decisions

- Draft Daily Entries remain editable with incomplete grading.
- Submit and manager adjustment require exact reconciliation; no API client can
  bypass it.
- Cracked and Dirty are saleable by default on a new installation and
  independently configurable per farm.
- Discarded remains non-saleable.
- A farm prices Cracked and Dirty through normal, separate Products. There is
  no global discount percentage or automatic price calculation.
- A configuration change affects future submissions only. Historical entries
  keep their recorded disposition and are never backfilled into stock.

## Data model

`EggGrade` gains a stable `DailyEntryKind` (`Manual`, `Cracked`, or `Dirty`).
At most one grade of each quality kind exists per farm; a partial unique index
enforces that rule. The kind is immutable, so renaming a grade never changes
its meaning. The default Cracked and Dirty grades receive their matching kinds
and are saleable in fresh base data.

`DailyEntry` snapshots `CrackedQualityGradeId` and `DirtyQualityGradeId` when
it first becomes official. A null snapshot records a non-saleable condition.
The snapshot—not the current grade setting—governs later reporting, adjustment,
and lot reconciliation. Existing official entries initialise both snapshots as
null.

Both snapshot ids and each grade's `DailyEntryKind` are part of the public API
contract (`DailyEntryResponse`, `EggGradeResponse`) and their TypeScript
mirrors, not domain-internal state: the History UI must derive its
reconciliation feedback from an entry's own snapshot, never from the egg-grade
catalog's current state, or a later saleability change would retroactively
rewrite how a past entry reads.

## Write path

The manual Grading pane excludes the two configured quality-condition grades,
and the **record and adjust handlers refuse a manual line naming either of
them**. The pane is an affordance; the handler check is the guarantee, and
both are required. Manual-line eligibility is gated on `IsSaleable` alone with
no kind restriction, and this feature makes Cracked and Dirty saleable by
default — so with UI-only exclusion a direct or stale API client could name a
Cracked id as a manual line, pass the exact-total check (it is only a sum),
and produce **two lots for one grade**: the manual line's and the
counter-backed one. That double-counts the day's stock and breaks the
one-lot-per-grade assumption reconciliation depends on.

Its target is always `total - cracked - dirty - discarded`. On submit, the
domain verifies that exact total, then creates one lot per manual grade line
plus one lot for each quality snapshot whose counter is positive that day. A
non-null snapshot is always recorded, independent of its counter — later
adjustments and historical reporting need to know which grade a condition
represented even on a zero-count day — but `EggLot.Create` rejects a zero or
negative quantity, so a zero counter must never reach lot creation. A
zero-cracked day and a zero-dirty day are each an explicit, tested case: the
snapshot is recorded and only that condition's lot is skipped. On adjustment,
the same invariant applies and all linked lots, including quality lots,
reconcile in the existing transaction and retain sold-floor protection.

This preserves one accounting identity: every collected egg is discarded, a
non-saleable condition loss, or represented by exactly one stock lot.

## UI and pricing

Daily Entry and the History adjustment dialog share the same counts/grading
hierarchy and live reconciliation feedback. The adjustment dialog has no draft
state, so its save control is disabled until the exact target and reason are
present. The Products screen continues to map one product to one saleable
grade. Products mapped to Cracked and Dirty set their own optional default
price; an operator may price them lower, equal, or leave a price unset.

## Compatibility and operations

The follow-on migration must add condition identities without overwriting a
farm's existing `IsSaleable` choice. The hand-maintained `InitialCreate` base
seed is updated for fresh databases only, so its Cracked and Dirty defaults are
saleable. Existing installations receive the new identities but retain their
current saleability and opt in explicitly. No historical lots are invented.

No migration or grandfather path is needed for a pre-existing incomplete
official entry or a pre-existing manual Cracked/Dirty grade line. This
application has never been deployed, and #245 squashed schema history to one
`InitialCreate` migration, so a virgin database is the only starting state —
there is no production data to grandfather. Two narrower checks confirm
nothing else stands in for it:

- **A pre-existing incomplete official entry, adjusted after this feature
  lands.** The exact-reconciliation gate on Submit/`ManagerAdjust`
  (`manualGrades == total - cracked - dirty - discarded`) is #394's rule,
  shared with PR #400 — whichever of the two lands first establishes it, and
  nothing here changes what it requires. What this design adds on top is
  quality-lot creation/reconciliation, gated on a non-null quality snapshot.
  Every entry that predates this feature, complete or incomplete, snapshots
  null for both conditions (see Data model), so that addition is a no-op on
  their adjustment path: it neither blocks nor unblocks anything the shared
  #394 rule didn't already decide. Whether an entry submitted under the old
  lenient rule — including the explicitly supported no-grade case
  `SubmitDailyEntryTests.Submit_WithoutGrades_Succeeds_NoLots` — can later be
  adjusted is #394's own compatibility question, unaffected by whether this
  feature has landed.
- **A pre-existing manual grade line against the Cracked or Dirty grade.**
  Cracked and Dirty already exist in `InitialCreate`'s base reference data
  today, as ordinary `GradeType = 'Quality'` grades seeded `IsSaleable =
  FALSE`. `RecordDailyEntryHandler` gates a manual grade line on `IsSaleable`
  alone, with no `GradeType` restriction, so the capability to grade into them
  manually has existed since #245 — a farm need only mark one saleable through
  the existing egg-grade endpoint. Nothing exercises that capability today:
  `DemoDataSeeder` and `SimulationDataSeeder` are the only writers that submit
  entries outside a human operator, and both draw manual grade lines only from
  `Large`/`Medium`/`Small` by name — Cracked and Dirty are never candidates,
  both because neither seeder selects them and because they seed
  non-saleable. There is no deployed farm to have done it by hand either. The
  one place it could theoretically exist is a developer's own local dev
  database, hand-poked through the SPA and never reset — and that is already
  covered by the standing remedy AGENTS.md prescribes for this exact class of
  dev-database/migration mismatch: drop and recreate (`docker compose -f
  deploy/docker-compose.dev.yml down -v && docker compose -f
  deploy/docker-compose.dev.yml up -d`), the same fix already required at the
  #245 squash boundary. This feature does not need its own migration or
  preservation logic for that case.

Because this changes the Daily Entry write contract, implementation must update
and verify every non-CI writer and fixture named in PR #395: demo/simulation
seeders, k6 bundles, and the Playwright Manager and Worker flows. The
simulation manifest must change when fixture totals or lots change.

## Out of scope

- Food-safety eligibility rules and jurisdiction-specific restrictions.
- Automatic discount formulas, price levels, or multi-grade products.
- A later regrade/repack workflow.
