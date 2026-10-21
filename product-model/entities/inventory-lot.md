---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/GeneralInventory/Inventory/InventoryLot.cs
---

# Inventory lot

A received batch of an inventory item, created by recording a purchase.

## Information kept

- **Received date** — the farm day it arrived
- **Quantity received** — the decimal quantity received
- **Remaining** — what is left after usage and corrections; never below what has been consumed
- **Unit cost** — the lot's cost per unit
- **Supplier lot number** — optional
- **Expiry** — an optional expiry date, which may be in the future
