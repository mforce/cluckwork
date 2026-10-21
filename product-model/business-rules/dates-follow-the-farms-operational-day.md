---
appliesTo:
- type: entity
  id: farm
  facts:
  - Timezone
- type: capability
  id: save-daily-entry-draft
- type: capability
  id: confirm-sales-order
- type: capability
  id: record-bird-movement
- type: capability
  id: record-payment
- type: capability
  id: record-expense
- type: capability
  id: create-sales-order
- type: capability
  id: record-purchase
- type: capability
  id: record-feed-usage
- type: capability
  id: record-water-usage
- type: capability
  id: deplete-flock
- type: capability
  id: archive-flock
references:
- kind: doc
  role: context
  target: docs/decisions/264-farm-timezone.md
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# Every date rule reads the farm's own today

Whether a record is dated in the future, which lots are restricted or sellable, and the day a flock is depleted or archived on are all judged against the calendar date in the farm's timezone, with no slack either side.

## Rationale

At 18:00 in Los Angeles it is already tomorrow in UTC; a UTC boundary would release eggs from a withdrawal period a day early.
