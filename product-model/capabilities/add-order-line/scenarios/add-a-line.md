---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person picks a product and a unit and enters a whole number of units, seeing the egg count it makes
  kind: actor
  actor: sales
  entities:
  - entity: product
    effect: reads
    facts:
    - Name
    - Selling unit
    - Default price
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product prefills the unit price from the list price scaled to the unit, rounded up to the currency's smallest unit
  kind: product
  actor: sales
  entities:
  - entity: product
    effect: reads
    facts:
    - Default price
    - Selling unit
  - entity: packed-unit
    effect: reads
    facts:
    - Eggs per unit
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Sales person keeps or changes the unit price and adds the line
  kind: actor
  actor: sales
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product adds the line with its grade, eggs per unit and list price as of now, and recalculates the order total
  kind: product
  actor: sales
  entities:
  - entity: sales-order
    effect: changes
    from: Draft
    to: Draft
    facts:
    - Lines
    - Total
  - entity: product
    effect: reads
    facts:
    - Egg grade
  - entity: packed-unit
    effect: reads
    facts:
    - Eggs per unit
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Add a line

## Trigger

The customer wants a quantity of a product.

## Outcome

The Draft order carries the line and its new total.

## Edge cases

- Re-pointing a product's grade or redefining a packed unit later never changes a recorded line.
- A line whose product has no default price, or whose price is in another currency, records that no list price is comparable.
