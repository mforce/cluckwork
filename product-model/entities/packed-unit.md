---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Catalog/EggUnitConversion.cs
---

# Packed unit

How many eggs one selling unit holds on this farm: dozen, flat, tray, carton or case, with individual always one egg.

## Information kept

- **Unit** — the selling unit
- **Eggs per unit** — eggs one unit holds; individual is always 1 and fixed
- **Active** — whether the unit can be chosen on new sales lines and as a counting unit
