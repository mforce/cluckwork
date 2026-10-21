---
appliesTo:
- type: capability
  id: confirm-sales-order
- type: entity
  id: sales-order
  facts:
  - Allocations
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggLots/SaleAllocationPlanner.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#Allocate
---

# A confirmed sale draws each grade's oldest lots first

Confirming draws each line's eggs from its grade's lots ordered by production date, oldest first, under a lock so two sales can never allocate the same eggs. Lots dated after the farm's today are never drawn. The whole order is planned before anything is drawn, so a refusal draws nothing.
