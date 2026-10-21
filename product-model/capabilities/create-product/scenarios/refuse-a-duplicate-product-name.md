---
kind: validation
routes:
  web: Web
steps:
- text: The Manager enters a name another product already has, in any case
  kind: actor
  actor: manager
  entities:
  - entity: product
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::products
- text: The Product refuses the new entry
  kind: condition
  actor: manager
  entities:
  - entity: product
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::setup::products
---

# Refuse a duplicate product name

## Trigger

Two products would share a name.

## Outcome

Nothing is created.
