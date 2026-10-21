---
kind: validation
routes:
  web: Web
steps:
- text: The Manager enters a name another grade already has, in any case
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::setup::grades
- text: The Product refuses the grade
  kind: condition
  actor: manager
  entities:
  - entity: egg-grade
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::setup::grades
---

# Refuse a duplicate grade name

## Trigger

Two grades would share a name.

## Outcome

No grade is created.
