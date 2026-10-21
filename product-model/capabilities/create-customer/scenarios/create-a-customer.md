---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person chooses New customer and enters a name and phone, with any email, address or note
  kind: actor
  actor: sales
  entities:
  - entity: customer
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::customers
- text: The Product creates the customer
  kind: product
  actor: sales
  entities:
  - entity: customer
    effect: creates
    facts:
    - Name
    - Phone
    - Email
    - Address
    - Note
  contexts:
    web:
      place: farm-web::farm::customers
---

# Create a customer

## Trigger

The farm starts selling to someone new.

## Outcome

The customer can be picked on new sales orders.
