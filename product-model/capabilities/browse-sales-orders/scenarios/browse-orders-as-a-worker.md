---
kind: alternative
routes:
  web: Web
steps:
- text: The Worker opens the order list
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales
- text: The Product lists the orders without any outstanding amount
  kind: product
  actor: worker
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Reference
    - Order date
    - Total
    - Discount
  contexts:
    web:
      place: farm-web::farm::sales
---

# Browse orders as a Worker

## Trigger

A Worker who builds orders reviews them.

## Outcome

They see orders but not what is owed.

## Edge cases

- Asking for unpaid orders only as a Worker is refused rather than ignored.
