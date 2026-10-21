---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses activate on a deactivated product
  kind: actor
  actor: manager
  entities:
  - entity: product
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::setup::products
- text: The Product activates it
  kind: product
  actor: manager
  entities:
  - entity: product
    effect: changes
    from: Inactive
    to: Active
    facts: []
  contexts:
    web:
      place: farm-web::setup::products
---

# Activate a product

## Trigger

A product returns to sale.

## Outcome

It appears on new lines again.
