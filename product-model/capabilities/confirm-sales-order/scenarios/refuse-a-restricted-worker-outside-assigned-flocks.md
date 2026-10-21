---
kind: validation
routes:
  web: Web
steps:
- text: The Worker, restricted by assignment under the policy that limits them to their assigned birds, confirms an order those birds cannot cover
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product refuses with a generic message that their assigned stock falls short and that selling from elsewhere can be allowed in settings, naming no source, grade or quantity
  kind: condition
  actor: worker
  entities:
  - entity: farm
    effect: reads
    facts:
    - Worker sale allocation
  - entity: egg-lot
    effect: reads
    facts:
    - Available
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Refuse a Worker whose assigned flocks lack the stock

## Trigger

Other flocks hold enough stock but the Worker may not draw on them.

## Outcome

The order stays a Draft and the Worker learns nothing about stock outside their flocks.

## Edge cases

- When the whole farm is short too, the Worker gets a generic insufficient-stock message without grade or quantity detail.
