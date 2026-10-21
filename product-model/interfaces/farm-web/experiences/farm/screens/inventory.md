---
entities:
- entity: inventory-item
  shows:
  - Name
  - Category
  - Unit of measure
  - Default unit cost
  - Stock on hand
  collects:
  - Name
  - Category
  - Unit of measure
  - Default unit cost
- entity: inventory-lot
  shows:
  - Received date
  - Quantity received
  - Remaining
  - Unit cost
  - Supplier lot number
  - Expiry
  collects:
  - Received date
  - Quantity received
  - Unit cost
  - Supplier lot number
  - Expiry
- entity: inventory-movement
  shows:
  - Date
  - Type
  - Quantity
  - Reason
entryPoints:
- farm-web: /inventory
references:
- kind: code
  role: implementation
  target: web/src/routes/InventoryPage.tsx
---

# Inventory

The farm's inventory catalog with stock on hand, and each item's lots and ledger, where purchases and stock corrections are recorded.
