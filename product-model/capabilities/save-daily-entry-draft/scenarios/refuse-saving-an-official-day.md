---
kind: validation
routes:
  web: Web
steps:
- text: The Worker opens a flock's day that has already been submitted
  kind: actor
  actor: worker
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product shows the day as already official and refuses any save; corrections are made from History
  kind: condition
  actor: worker
  entities:
  - entity: daily-entry
    effect: reads
    facts: []
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
---

# Refuse saving over an official day

## Trigger

Someone tries to record a day whose egg lots already exist.

## Outcome

The official numbers are unchanged; an Owner or Manager can adjust or void the day from History.
