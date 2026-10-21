---
relations:
- entity: egg-movement
  verb: records
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#EggLot
---

# Egg lot

A batch of sellable eggs: one flock's eggs of one grade from one production day, created when a daily entry becomes official. Stock is the sum of lots' available eggs per grade.

## Information kept

- **Production date** — the day the eggs were laid
- **Flock** — the flock that laid them
- **Produced** — eggs the daily entry put in the lot
- **Available** — eggs still in stock: produced less sold, allocated and written off, never below zero
- **Restricted until** — an optional medication-withdrawal date through which the lot's eggs show as restricted and cannot be sold
