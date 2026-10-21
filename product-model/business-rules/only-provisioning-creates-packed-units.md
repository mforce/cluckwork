---
appliesTo:
- type: entity
  id: packed-unit
  effect: creates
permits:
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Commerce/Repositories/EggUnitConversionProvisioning.cs
---

# Only farm provisioning creates packed units

A farm's six packed units (individual, dozen, flat, tray, carton and case) are created with the farm; nobody adds more.
