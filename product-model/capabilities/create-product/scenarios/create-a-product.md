---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses New product and enters its name, grade, selling unit and any default price
  kind: actor
  actor: manager
  entities:
  - entity: product
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::products
- text: The Product creates the product as Active with the farm's currency
  kind: product
  actor: manager
  entities:
  - entity: product
    effect: creates
    to: Active
    facts:
    - Name
    - Product type
    - Egg grade
    - Selling unit
    - Default price
    - Currency
    - Notes
  - entity: farm
    effect: reads
    facts:
    - Currency
  contexts:
    web:
      place: farm-web::setup::products
---

# Create a product

## Trigger

The farm starts selling a new egg product.

## Outcome

The product is offered on new order lines.
