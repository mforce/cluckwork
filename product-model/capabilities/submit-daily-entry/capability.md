---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: daily-entry
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/SubmitDailyEntry/SubmitDailyEntryHandler.cs#SubmitDailyEntryHandler
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/DailyEntry.cs#Submit
- kind: code
  role: implementation
  target: web/src/routes/dailyEntry/EntryFooter.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/DailyEntryTools.cs
---

# Submit a day

Make a flock's day official. Submit day saves the day's numbers and submits them in one act, after a confirmation: the grading must add up to exactly the sellable count, and submitting creates one egg lot per graded grade and a mortality movement for the day's deaths. A connected app with the Submit daily entries permission submits a Draft the same way, naming it by its id or by flock and date.
