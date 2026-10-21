---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: daily-entry
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/RecordDailyEntry/RecordDailyEntryHandler.cs#RecordDailyEntryHandler
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/RecordDailyEntry/RecordDailyEntryValidator.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/DailyEntryTools.cs
- kind: code
  role: implementation
  target: web/src/routes/dailyEntry/useDailyEntrySave.ts
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# Save a daily entry draft

Record one flock's day as a Draft: the egg counts, the losses set aside, mortality and as much of the grading as is done. A draft may be graded partly or not at all, and saving the same flock and day again replaces its numbers. A connected app with the Record daily entries permission records drafts too.
