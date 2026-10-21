---
appliesTo:
- type: capability
  id: save-daily-entry-draft
- type: entity
  id: daily-entry
  facts:
  - Grading
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/DailyEntry.cs#RecordProduction
---

# A draft never grades more eggs than survived the day

A Draft may be graded partly or not at all, but its grading may not exceed total eggs minus cracked, dirty and discarded, and the losses together may not exceed the total.
