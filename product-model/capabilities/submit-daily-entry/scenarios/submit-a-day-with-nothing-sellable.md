---
kind: alternative
routes:
  web: Web
steps:
- text: The Worker records a day whose cracked, dirty and discarded eggs equal the total, with no grading, and chooses Submit day
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product saves and submits the day without putting any eggs in stock
  kind: product
  actor: worker
  entities:
  - entity: daily-entry
    effect: creates
    to: Submitted
    facts:
    - Date
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Mortality
    - Grading
    - Condition grades
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
---

# Submit a day where every egg was lost

## Trigger

A flock's whole collection was lost.

## Outcome

The day is official with zero sellable eggs; zero sellable reconciles to no grading.
