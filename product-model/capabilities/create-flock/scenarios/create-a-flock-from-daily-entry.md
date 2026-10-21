---
kind: alternative
routes:
  web: Web
steps:
- text: The Manager chooses new flock while recording a day and enters its name, breed, placement date and bird count
  kind: actor
  actor: manager
  entities:
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product creates the flock as Active and selects it for the day being recorded
  kind: product
  actor: manager
  entities:
  - entity: flock
    effect: creates
    to: Active
    facts:
    - Name
    - Breed
    - Placement date
    - Initial count
  contexts:
    web:
      place: farm-web::farm::daily-entry
---

# Create a flock while recording a day

## Trigger

A Manager starts recording a day for birds that have no flock yet.

## Outcome

The new flock is chosen and recording continues.
