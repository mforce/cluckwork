---
kind: validation
routes:
  web: Web
steps:
- text: The Manager leaves the breed blank or enters no birds
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::flocks
- text: The Product refuses to create the flock
  kind: condition
  actor: manager
  entities:
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Refuse a flock without breed or birds

## Trigger

A flock is missing what defines it.

## Outcome

No flock is created.
