---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/GeneralInventory/Inventory/InventoryMovement.cs
---

# Inventory movement

One signed row of an inventory item's append-only ledger: every purchase, usage, adjustment and discard.

## Information kept

- **Date** — the farm day of the movement
- **Type** — Purchase, Usage, Adjustment or Discard
- **Quantity** — the signed quantity
- **Reason** — the reason given for an adjustment or discard
