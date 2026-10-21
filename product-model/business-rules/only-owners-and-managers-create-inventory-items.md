---
appliesTo:
- type: entity
  id: inventory-item
  effect: creates
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/GeneralInventory/Inventory/InventoryEndpoints.cs
---

# Only Owners and Managers create an inventory item

The inventory catalog is operational administration.
