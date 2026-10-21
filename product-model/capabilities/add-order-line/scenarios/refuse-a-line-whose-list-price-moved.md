---
kind: validation
routes:
  web: Web
steps:
- text: The Sales person adds a line after its list price changed since the form loaded it
  kind: actor
  actor: sales
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product refuses the line and asks for the price to be re-checked
  kind: condition
  actor: sales
  entities:
  - entity: product
    effect: reads
    facts:
    - Default price
  - entity: sales-order
    effect: reads
    facts:
    - Lines
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Refuse a line whose list price moved

## Trigger

The catalogue price changed while the seller was working.

## Outcome

No line is added until the seller re-checks.

## Edge cases

- A changed eggs-per-unit definition is refused the same way.
- An inactive product, or one whose grade is inactive or not saleable, cannot go on a new line.
- A unit with no active eggs-per-unit definition cannot be sold.
- A product with no default price needs a unit price entered.
