---
availability:
- place: farm-web::farm
- place: farm-mcp
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/RecordFeedUsage/RecordFeedUsageHandler.cs
- kind: code
  role: implementation
  target: web/src/routes/FeedPage.tsx
- kind: code
  role: implementation
  target: web/src/routes/FeedCaptureForm.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/InventoryTools.cs
---

# Record feed usage

Log that a flock was fed a quantity of a feed, supplement or additive on a day. The quantity is drawn from the item's lots oldest first, only from lots received by that day, and the record carries the estimated cost of the lots it consumed. Feed usages cannot be edited. A connected app with the Record farm operations permission records feed usage too, and it counts at once.
