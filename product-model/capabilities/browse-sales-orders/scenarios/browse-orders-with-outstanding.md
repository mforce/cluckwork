---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person opens Sales and filters by status and customer, or ticks Unpaid only
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
- text: The Product lists the matching orders with total, outstanding and discount, counting only confirmed orders with something left to pay as unpaid
  kind: product
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Reference
    - Order date
    - Total
    - Discount
    - Outstanding
  - entity: customer
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::sales
---

# Browse orders and what they owe

## Trigger

Someone reviews orders or chases payment.

## Outcome

They see each order's figures; Draft, Cancelled and Voided orders carry no outstanding amount.
