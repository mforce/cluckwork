---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person chooses New order, picks the customer and the order date
  kind: actor
  actor: sales
  entities:
  - entity: customer
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::sales
- text: The Product creates a Draft order with a generated reference in the farm's currency and opens it
  kind: product
  actor: sales
  entities:
  - entity: sales-order
    effect: creates
    to: Draft
    facts:
    - Reference
    - Order date
    - Currency
    - Total
  - entity: customer
    effect: reads
    facts: []
  - entity: farm
    effect: reads
    facts:
    - Currency
  contexts:
    web:
      place: farm-web::farm::sales
---

# Create a draft order

## Trigger

A customer wants eggs.

## Outcome

An empty Draft order is open for lines.
