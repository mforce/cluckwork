---
kind: primary
routes:
  web: Web
steps:
- text: The Owner opens the flocks of a person who is a plain worker, picks one and re-enters their own current password
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Email
  - entity: flock-assignment
    effect: reads
    facts:
    - Flock
  - entity: worker
    effect: reads
    facts: []
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product adds the assignment
  kind: product
  actor: owner
  entities:
  - entity: flock-assignment
    effect: creates
    facts:
    - Flock
  - entity: user
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
---

# Assign a flock

## Trigger

A Worker should look after particular flocks.

## Outcome

The Worker reads and records only their assigned flocks from their next request.
