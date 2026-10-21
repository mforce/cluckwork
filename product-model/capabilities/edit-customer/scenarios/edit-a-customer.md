---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person chooses edit on a customer and changes their details
  kind: actor
  actor: sales
  entities:
  - entity: customer
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::customers
- text: The Product saves the customer's new details
  kind: product
  actor: sales
  entities:
  - entity: customer
    effect: changes
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

# Edit a customer

## Trigger

A customer's details change.

## Outcome

Their orders show the current name.

## Edge cases

- Someone saving against a stale copy of the customer is refused and reloads.
