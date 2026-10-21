---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/FlockManagement/Flocks/BirdMovement.cs#BirdMovement
---

# Bird movement

One row of a flock's append-only bird ledger: birds that died, were culled or were recounted.

## Information kept

- **Date** — the farm day it happened; it takes effect from the next day
- **Type** — Mortality, Cull or Adjustment
- **Quantity** — birds removed; only an Adjustment may be negative, adding birds back
- **Note** — an optional note
