---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/GeneralInventory/Inventory/FeedUsage.cs
---

# Feed usage

A feeding event: one flock fed a quantity of one feed, supplement or additive on a day. Feed usages are create-only.

## Information kept

- **Date** — the farm day fed
- **Quantity** — the decimal quantity fed
- **Estimated cost** — the cost of the actual lots consumed, oldest first
- **Note** — optional
- **Daily entry link** — the non-voided daily entry that existed for that flock and day when the usage was recorded, if any
