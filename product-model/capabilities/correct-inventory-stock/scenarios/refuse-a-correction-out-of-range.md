---
kind: validation
routes:
  web: Web
steps:
- text: The Manager removes more than the lot has left, or adds back more than it received
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product refuses; genuinely new stock is a purchase
  kind: condition
  actor: manager
  entities:
  - entity: inventory-lot
    effect: reads
    facts:
    - Remaining
    - Quantity received
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Refuse a correction out of range

## Trigger

A correction would take a lot below what was consumed or above what it received.

## Outcome

The lot is unchanged.

## Edge cases

- A correction dated before the lot was received, or after the farm's today, is refused.
