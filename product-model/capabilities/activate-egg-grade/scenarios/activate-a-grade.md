---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses activate on a deactivated grade
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
- text: The Product activates the grade
  kind: product
  actor: manager
  entities:
  - entity: egg-grade
    effect: changes
    from: Inactive
    to: Active
    facts: []
  contexts:
    web:
      place: farm-web::setup::grades
---

# Activate a grade

## Trigger

A retired grade is needed again.

## Outcome

The grade is back in pickers and its remaining stock can go on new lines.
