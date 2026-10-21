---
kind: validation
routes:
  web: Web
steps:
- text: The Sales person leaves the name or phone blank
  kind: actor
  actor: sales
  entities: []
  contexts:
    web:
      place: farm-web::farm::customers
- text: The Product refuses to create the customer
  kind: condition
  actor: sales
  entities:
  - entity: customer
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::customers
---

# Refuse a customer without name or phone

## Trigger

A customer is missing a required detail.

## Outcome

No customer is created.
