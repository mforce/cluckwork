---
appliesTo:
- type: capability
  id: submit-daily-entry
- type: capability
  id: adjust-daily-entry
- type: capability
  id: void-daily-entry
- type: capability
  id: confirm-sales-order
- type: capability
  id: void-sales-order
- type: capability
  id: write-off-stock
- type: entity
  id: egg-movement
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggInventoryMovement.cs
---

# Every change to an egg lot's available eggs is recorded in its movement ledger

Production, each sale allocation, voids, adjustments and write-offs are signed rows written with the change, so a lot's available eggs always equal the sum of its movements.
