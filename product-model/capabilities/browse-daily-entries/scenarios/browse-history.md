---
kind: primary
routes:
  web: Web
steps:
- text: The Read-only person opens History and narrows it by flock and dates
  kind: actor
  actor: read-only
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product lists the matching daily entries newest first with their status, counts, sellable eggs and grading
  kind: product
  actor: read-only
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Date
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Mortality
    - Sellable
    - Grading
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::history
---

# Browse History

## Trigger

Someone reviews recorded days.

## Outcome

They see each day's numbers and status, and the reason on any adjusted or voided day.

## Edge cases

- Each row's record history names who created it and who last changed it, and when the day became official.
- A Draft row links back to Daily entry through edit.
