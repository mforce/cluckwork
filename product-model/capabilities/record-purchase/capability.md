---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: inventory
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/RecordPurchase/RecordPurchaseHandler.cs
- kind: code
  role: implementation
  target: web/src/routes/InventoryItemPanel.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/InventoryTools.cs
---

# Record a purchase

Receive a batch of an active item: received date, quantity, unit cost and optional supplier lot number and expiry. Each purchase is a new lot. A connected app with the Record farm operations permission records purchases too, and they count at once.
