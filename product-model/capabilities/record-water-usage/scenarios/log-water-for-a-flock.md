---
kind: primary
routes:
  web: Web
steps:
- text: The Worker chooses Log water, picks the flock and day, and enters a quantity or meter readings and the source
  kind: actor
  actor: worker
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::water
- text: The Product records the water usage with any same-day daily entry link
  kind: product
  actor: worker
  entities:
  - entity: water-usage
    effect: creates
    facts:
    - Date
    - Quantity
    - Unit
    - Source
    - Meter readings
    - Note
    - Daily entry link
  - entity: flock
    effect: reads
    facts: []
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::water
---

# Log water for a flock

## Trigger

A flock's water is read.

## Outcome

The water history shows the usage.
