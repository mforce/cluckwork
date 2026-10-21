---
availability:
- place: farm-web::farm
domain: inventory
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/SetInventoryItemActiveHandler.cs
---

# Deactivate an inventory item

Stop new purchases of an item; its remaining stock can still be used up.
