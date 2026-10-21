---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/GeneralInventory/Inventory/WaterUsage.cs
---

# Water usage

Water a flock consumed on a day, entered directly or from meter readings. The one create-then-edit record type.

## Information kept

- **Date** — the farm day; fixed after creation
- **Quantity** — litres or gallons, or the meter delta
- **Unit** — L or gal
- **Source** — Well, Municipal, Tank or Other
- **Meter readings** — optional start and end readings, which must agree with a quantity when both are given
- **Note** — optional
- **Daily entry link** — the non-voided daily entry that existed for that flock and day when the usage was recorded, if any
