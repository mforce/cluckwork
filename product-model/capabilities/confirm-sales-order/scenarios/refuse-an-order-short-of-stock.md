---
kind: validation
routes:
  web: Web
steps:
- text: The Sales person chooses Confirm on the Draft order and confirms
  kind: actor
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Total
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product refuses to confirm, naming the grade and how many eggs could not be allocated; nothing is drawn
  kind: condition
  actor: sales
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Available
  - entity: sales-order
    effect: reads
    facts:
    - Lines
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Refuse an order short of stock

## Trigger

The grade's sellable stock cannot cover a line.

## Outcome

The order stays a Draft with all stock untouched.
