---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: history
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/EggOperations/DailyEntries/DailyEntryEndpoints.cs
- kind: code
  role: implementation
  target: web/src/routes/HistoryPage.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/DailyEntryTools.cs
---

# Browse daily entries

Read the farm's daily entries by flock and date range with their status and numbers. A Worker restricted to assigned flocks sees only those flocks' entries.
