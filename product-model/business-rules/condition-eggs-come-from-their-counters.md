---
appliesTo:
- type: capability
  id: save-daily-entry-draft
- type: capability
  id: adjust-daily-entry
- type: entity
  id: egg-grade
  facts:
  - Condition counter
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/ConditionGradeGuard.cs
---

# Cracked and Dirty eggs are counted only by their counters, never as grade lines

A Cracked or Dirty grade cannot appear as a hand-graded line, so a condition egg never makes a second lot for the same grade and day. Which grade each counter resolved to is fixed when the day becomes official.
