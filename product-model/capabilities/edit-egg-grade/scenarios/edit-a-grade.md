---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses edit on a grade and changes its name, sort order or saleability
  kind: actor
  actor: manager
  entities:
  - entity: egg-grade
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::setup::grades
- text: The Product saves the grade
  kind: product
  actor: manager
  entities:
  - entity: egg-grade
    effect: changes
    facts:
    - Name
    - Sort order
    - Saleable
  contexts:
    web:
      place: farm-web::setup::grades
---

# Edit a grade

## Trigger

A grade needs renaming or reordering.

## Outcome

History keeps resolving the grade by its current name.

## Edge cases

- Renaming the Cracked or Dirty grade keeps it fed by its counter: the binding is by kind, not name.
