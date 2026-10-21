---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses activate on a deactivated item
  kind: actor
  actor: manager
  entities:
  - entity: inventory-item
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product activates the item
  kind: product
  actor: manager
  entities:
  - entity: inventory-item
    effect: changes
    from: Inactive
    to: Active
    facts: []
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Activate an item

## Trigger

An item is stocked again.

## Outcome

The item accepts purchases.
