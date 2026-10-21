---
appliesTo:
- type: capability
  id: confirm-sales-order
- type: capability
  id: view-stock
- type: entity
  id: egg-lot
  facts:
  - Restricted until
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#IsRestricted
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggLotRepository.cs
---

# An egg lot under withdrawal restriction is never allocated to a sale

A lot whose restricted-until date is on or after the farm's today counts as restricted, not available, on Stock and against low-stock floors, and confirming an order skips it. Restriction does not block a write-off, and eggs returned by a void keep their lot's restriction.

## Rationale

Eggs from birds inside a medication withholding period must not leave the farm.
