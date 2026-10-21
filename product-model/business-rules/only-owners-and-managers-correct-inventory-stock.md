---
appliesTo:
- type: entity
  id: inventory-lot
  effect: changes
  contexts:
  - place: farm-web::farm::inventory
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/GeneralInventory/Inventory/InventoryEndpoints.cs
---

# Only Owners and Managers correct an inventory lot

Stock corrections on Inventory are the corrective tier; feed usage draws lots down as production work.
