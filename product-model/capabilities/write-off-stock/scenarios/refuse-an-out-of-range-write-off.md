---
kind: validation
routes:
  web: Web
steps:
- text: The Manager removes more eggs than the lot has available, or adds back more than earlier write-offs removed
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::stock
- text: The Product refuses the correction; a recount above production is a daily-entry adjustment
  kind: condition
  actor: manager
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Produced
    - Available
  contexts:
    web:
      place: farm-web::farm::stock
---

# Refuse a correction outside the lot's range

## Trigger

A correction would push available eggs below zero or past what can be recounted.

## Outcome

The lot is unchanged.

## Edge cases

- Eggs allocated to a confirmed order never count as recount headroom.
- A correction without a reason is refused.
