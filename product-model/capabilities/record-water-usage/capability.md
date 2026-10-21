---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: water
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/RecordWaterUsage/RecordWaterUsageHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/RecordWaterUsage/RecordWaterUsageValidator.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/InventoryTools.cs
---

# Record water usage

Log a flock's water for a day, as a direct quantity in litres or gallons or from start and end meter readings, which must agree with a quantity when both are given. A connected app with the Record farm operations permission records water usage too, and it counts at once.
