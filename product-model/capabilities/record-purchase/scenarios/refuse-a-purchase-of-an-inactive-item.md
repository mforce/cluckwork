---
kind: validation
routes:
  web: Web
steps:
- text: The Worker records a purchase of a deactivated item
  kind: actor
  actor: worker
  entities:
  - entity: inventory-item
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product refuses because stock cannot be received for an inactive item
  kind: condition
  actor: worker
  entities:
  - entity: inventory-item
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Refuse a purchase of an inactive item

## Trigger

Someone receives stock of a retired item.

## Outcome

No lot is created.
