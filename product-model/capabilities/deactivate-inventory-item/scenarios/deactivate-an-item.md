---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses deactivate on an item
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
- text: The Product deactivates the item
  kind: product
  actor: manager
  entities:
  - entity: inventory-item
    effect: changes
    from: Active
    to: Inactive
    facts: []
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Deactivate an item

## Trigger

The farm stops buying an item.

## Outcome

The item shows Inactive and refuses purchases.
