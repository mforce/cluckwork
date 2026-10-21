---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses deactivate on a grade
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
- text: The Product deactivates the grade
  kind: product
  actor: manager
  entities:
  - entity: egg-grade
    effect: changes
    from: Active
    to: Inactive
    facts: []
  contexts:
    web:
      place: farm-web::setup::grades
---

# Deactivate a grade

## Trigger

The farm stops using a grade.

## Outcome

The grade leaves pickers; its stock remains and can be sold only after reactivating.
