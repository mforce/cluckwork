---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses edit on a product and changes its details
  kind: actor
  actor: manager
  entities:
  - entity: product
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::setup::products
- text: The Product saves the product
  kind: product
  actor: manager
  entities:
  - entity: product
    effect: changes
    facts:
    - Name
    - Egg grade
    - Selling unit
    - Default price
    - Notes
  contexts:
    web:
      place: farm-web::setup::products
---

# Edit a product

## Trigger

A product is re-priced or re-pointed to another grade.

## Outcome

New order lines use the new details; recorded lines keep theirs.
