---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/VoidSale/VoidSaleHandler.cs#VoidSaleHandler
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#Restore
---

# Void a sales order

Undo a mistaken confirm with a reason: every allocated egg returns to the exact lot it came from, keeping that lot's place in the oldest-first order and any restriction. The order stays listed as Voided with its lines and total.
