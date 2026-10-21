---
availability:
- place: farm-web::farm
domain: inventory
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/CreateInventoryItem/CreateInventoryItemHandler.cs
---

# Create an inventory item

Add an item to the farm's inventory catalog with its category, unit of measure and an optional default unit cost.
