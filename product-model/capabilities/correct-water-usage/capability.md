---
availability:
- place: farm-web::farm
domain: water
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/UpdateWaterUsage/UpdateWaterUsageHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/GeneralInventory/Water/WaterUsageEndpoints.cs
---

# Correct water usage

Edit a water record in place. Its flock and date stay fixed.
