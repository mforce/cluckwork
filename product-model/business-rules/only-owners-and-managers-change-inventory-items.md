---
appliesTo:
- type: entity
  id: inventory-item
  effect: changes
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/GeneralInventory/Inventory/InventoryEndpoints.cs
---

# Only Owners and Managers change an inventory item

Editing, deactivating and activating items are operational administration.
