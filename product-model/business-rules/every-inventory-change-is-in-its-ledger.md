---
appliesTo:
- type: capability
  id: record-purchase
- type: capability
  id: record-feed-usage
- type: capability
  id: correct-inventory-stock
- type: entity
  id: inventory-movement
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/GeneralInventory/Inventory/InventoryMovement.cs
---

# Every purchase, usage and correction of inventory is recorded in its ledger

Rows are never edited or deleted; mistakes get a compensating row, and lots hold the balance the ledger explains.
