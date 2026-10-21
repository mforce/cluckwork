---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses New item and enters its name, category, unit of measure and any default cost
  kind: actor
  actor: manager
  entities:
  - entity: inventory-item
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product creates the item as Active
  kind: product
  actor: manager
  entities:
  - entity: inventory-item
    effect: creates
    to: Active
    facts:
    - Name
    - Category
    - Unit of measure
    - Default unit cost
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Create an item

## Trigger

The farm starts stocking something new.

## Outcome

The item can receive purchases.

## Edge cases

- A name another item already has, in any case, is refused.
