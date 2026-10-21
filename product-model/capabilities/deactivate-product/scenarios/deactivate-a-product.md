---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses deactivate on a product
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
- text: The Product deactivates it
  kind: product
  actor: manager
  entities:
  - entity: product
    effect: changes
    from: Active
    to: Inactive
    facts: []
  contexts:
    web:
      place: farm-web::setup::products
---

# Deactivate a product

## Trigger

The farm stops selling a product.

## Outcome

It no longer appears on new lines.
