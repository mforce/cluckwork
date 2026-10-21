---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/ConfirmSale/ConfirmSaleHandler.cs#ConfirmSaleHandler
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggLots/SaleAllocationPlanner.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggLotRepository.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#Allocate
- kind: doc
  role: context
  target: docs/designs/612-worker-sales-allocation-policy.md
---

# Confirm a sales order

Confirm a Draft order and allocate its eggs: each line draws from the oldest lots of its grade that are not under withdrawal restriction and were produced no later than the farm's today. The lots each line drew from are recorded.
