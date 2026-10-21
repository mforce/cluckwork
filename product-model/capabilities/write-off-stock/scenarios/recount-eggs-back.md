---
kind: alternative
routes:
  web: Web
steps:
- text: The Manager chooses write off on a lot, picks Reconciliation and adds eggs back with a reason
  kind: actor
  actor: manager
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Available
  contexts:
    web:
      place: farm-web::farm::stock
- text: The Product adds the eggs back to the lot, up to what earlier write-offs removed
  kind: product
  actor: manager
  entities:
  - entity: egg-lot
    effect: changes
    facts:
    - Available
  contexts:
    web:
      place: farm-web::farm::stock
---

# Add eggs back after a recount

## Trigger

A recount finds more eggs than recorded.

## Outcome

The lot's available eggs rise.
