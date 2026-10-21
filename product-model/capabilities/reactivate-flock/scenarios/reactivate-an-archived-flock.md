---
kind: alternative
routes:
  web: Web
steps:
- text: The Manager shows archived flocks and chooses reactivate on one
  kind: actor
  actor: manager
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::flocks
- text: The Product returns the flock to Active and clears both lifecycle dates
  kind: product
  actor: manager
  entities:
  - entity: flock
    effect: changes
    from: Archived
    to: Active
    facts:
    - Depleted on
    - Archived on
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Reactivate an archived flock

## Trigger

An archived flock's records need correcting or it was archived by mistake.

## Outcome

The flock is Active and back in pickers.
