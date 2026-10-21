---
relations:
- entity: egg-lot
  verb: grades
  cardinality: one-to-many
- entity: product
  verb: supplies
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggGrade.cs#EggGrade
---

# Egg grade

A farm's grading bucket: sizes such as Small to Jumbo, qualities such as Seconds, Cracked and Dirty, and custom buckets. The catalog is the farm's own.

## Information kept

- **Name** — unique within the farm, ignoring case
- **Grade type** — Size, Quality or Custom; fixed after creation
- **Sort order** — where the grade sits in lists
- **Saleable** — whether the grade can receive graded production and be sold
- **Condition counter** — Cracked or Dirty for the grade the Daily entry's Cracked or Dirty count feeds, otherwise none
- **Low-stock floor** — an optional warning point in eggs; zero is a real floor that never warns

## States

### Active

Offered in capture and order pickers.

### Inactive

Removed from pickers; existing stock stays counted and lines added before deactivation can still confirm.
