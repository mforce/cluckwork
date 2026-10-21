---
availability:
- place: farm-web::farm
domain: inventory
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/RecordAdjustment/RecordAdjustmentHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/GeneralInventory/Inventory/InventoryLot.cs
---

# Correct inventory stock

Correct one lot of an inventory item with a reason: a negative adjustment fixes an over-entered purchase, a discard writes off spoiled stock, and a positive adjustment undoes an over-recorded usage, capped at the lot's received quantity.
