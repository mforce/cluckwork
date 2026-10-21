---
kind: validation
routes:
  web: Web
steps:
- text: The Worker enters a quantity and meter readings whose difference is not that quantity
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::water
- text: The Product refuses the record
  kind: condition
  actor: worker
  entities:
  - entity: water-usage
    effect: reads
    facts:
    - Quantity
    - Meter readings
  contexts:
    web:
      place: farm-web::farm::water
---

# Refuse readings that disagree with the quantity

## Trigger

The two ways of stating water disagree.

## Outcome

Nothing is recorded.

## Edge cases

- A day the flock does not accept, or a day after the farm's today, is refused.
