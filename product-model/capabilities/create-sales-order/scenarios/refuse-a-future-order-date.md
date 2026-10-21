---
kind: validation
routes:
  web: Web
steps:
- text: The Sales person picks an order date after today
  kind: actor
  actor: sales
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales
- text: The Product refuses to create the order
  kind: condition
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Order date
  - entity: farm
    effect: reads
    facts:
    - Timezone
  contexts:
    web:
      place: farm-web::farm::sales
---

# Refuse an order dated in the future

## Trigger

An order is dated ahead of the farm's day.

## Outcome

No order is created.
