---
appliesTo:
- type: entity
  id: flock
- type: capability
  id: save-daily-entry-draft
- type: capability
  id: submit-daily-entry
- type: capability
  id: adjust-daily-entry
- type: capability
  id: void-daily-entry
- type: capability
  id: record-feed-usage
- type: capability
  id: record-water-usage
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/FlockManagement/Flocks/Flock.cs#CanRecordProductionOn
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/VoidDailyEntry/VoidDailyEntryHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/AdjustDailyEntry/AdjustDailyEntryHandler.cs#AdjustDailyEntryHandler
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# A flock accepts records only in its capture window

An Active flock accepts daily entries, feed and water for any day up to the farm's today. A Depleted flock accepts backfill dated on or before its depletion date. An Archived flock accepts nothing. Corrections follow the same window: an official day, Locked included, can be adjusted or voided only while its flock would accept a record for that date, so an Archived flock's days, and a Depleted flock's days after its depletion date, wait until the flock is reactivated.
