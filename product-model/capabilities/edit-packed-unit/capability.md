---
availability:
- place: farm-web::setup
domain: products
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Catalog/UpdateEggUnitConversion/UpdateEggUnitConversionHandler.cs
- kind: code
  role: implementation
  target: web/src/routes/PackedUnitsPanel.tsx
---

# Edit a packed unit

Set how many eggs a selling unit holds on this farm and whether it is active. Individual is always one egg. Changing a unit affects only lines added afterwards.
