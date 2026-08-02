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

## Write path

The manual Grading pane excludes the two configured quality-condition grades.
Its target is always `total - cracked - dirty - discarded`. On submit, the
domain verifies that exact total, then creates lots for manual grades plus
non-null quality snapshots. On adjustment, the same invariant applies and all
linked lots, including quality lots, reconcile in the existing transaction and
retain sold-floor protection.

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

Because this changes the Daily Entry write contract, implementation must update
and verify every non-CI writer and fixture named in PR #395: demo/simulation
seeders, k6 bundles, and the Playwright Manager and Worker flows. The
simulation manifest must change when fixture totals or lots change.

## Out of scope

- Food-safety eligibility rules and jurisdiction-specific restrictions.
- Automatic discount formulas, price levels, or multi-grade products.
- A later regrade/repack workflow.
