# Cluckwork v4.3 — Rename Patch

## Change

The project/product name has been changed from:

```text
Egg Farm Manager
```

to:

```text
Cluckwork
```

## Scope unchanged

This is a naming patch only. The product scope, schema, workflows, build-readiness rules, and wireframe structure remain based on v4.2.

## Updated artifacts

- Specification title and product name
- Wireframe header labels
- HTML overview title/header
- Package name

---

# Cluckwork v4.4 — Egg-Farm Spec Corrections

## Changes

Three spec corrections from product review:

### §9.7 Egg unit conversions
Eggs are stored as individual eggs, but products are sold in packed units (dozen, tray, carton, case). Added a farm-configurable `egg_unit_conversions` table so farms define their own unit-to-egg factors. `base_unit_factor` is snapshotted on `sales_order_items` at line creation — same immutability principle as currency snapshots.

### §9.8 Grading and packing assumption
Documented that Phase 1 assumes eggs are graded at collection (candle/sort during daily entry). Reserved `egg_grade_transfers` with paired `grade_out`/`grade_in` movements for Phase 1.5 regrade/repack workflows without breaking the traceability chain.

### §19.3 Hen-day production fix
Original formula `total eggs / current live birds × 100` was incorrect:
- Used current (post-mortality) bird count, inflating the rate
- Included males and non-layer flocks

Corrected to `eggs collected / hen-days on date × 100` with:
- Point-in-time ledger-reconstructed laying-female count (`sex = 'female'`, `production_purpose = 'layer'`)
- Mixed-sex flocks flagged as approximate with reserved `female_bird_count` upgrade path
- Period anchored to `expected_start_lay_date`, reported by week-of-lay for industry layer-curve comparability
