---
appliesTo:
- type: entity
  id: water-usage
  effect: changes
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/GeneralInventory/Water/WaterUsageEndpoints.cs
---

# Only Owners and Managers correct a water record

Workers record water but do not edit it afterwards.
