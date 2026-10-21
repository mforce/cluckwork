---
appliesTo:
- type: capability
  id: submit-daily-entry
- type: entity
  id: egg-lot
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/SubmitDailyEntry/SubmitDailyEntryHandler.cs#SubmitDailyEntryHandler
---

# Submitting a day creates one egg lot per graded grade and its mortality movement

Egg lots are created only when a day becomes official: one lot per grade line, one for each condition count that resolved to an active saleable grade, and a mortality bird movement when deaths are above zero. Each lot links back to the day that created it.
