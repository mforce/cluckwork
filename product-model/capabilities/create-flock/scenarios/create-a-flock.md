---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses New flock and enters its name, breed, placement date and bird count
  kind: actor
  actor: manager
  entities:
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::flocks
- text: The Product creates the flock as Active
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
      place: farm-web::farm::flocks
---

# Create a flock

## Trigger

New birds are placed in a house.

## Outcome

The flock is Active and accepts daily entries, feed and water.
