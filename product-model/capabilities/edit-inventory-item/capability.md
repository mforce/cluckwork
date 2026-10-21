---
availability:
- place: farm-web::farm
domain: inventory
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/UpdateInventoryItem/UpdateInventoryItemHandler.cs
---

# Edit an inventory item

Change an item's name, unit of measure or default cost. The unit locks once any stock has been received.
