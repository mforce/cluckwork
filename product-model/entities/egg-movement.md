---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggInventoryMovement.cs
---

# Egg movement

One signed row of an egg lot's movement ledger: every change to the lot's available eggs, so the running sum always equals its balance.

## Information kept

- **When** — when the change was recorded
- **Type** — Production, Sale, Void, Adjustment, Discard, InternalUse or Reconciliation
- **Change** — the signed number of eggs
- **Reason** — the reason given for a write-off or recount
