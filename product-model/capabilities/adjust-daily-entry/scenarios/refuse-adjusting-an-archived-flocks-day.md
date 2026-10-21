---
kind: validation
routes:
  web: Web
steps:
- text: The Manager opens adjust on a day whose flock has been archived
  kind: actor
  actor: manager
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product refuses the adjustment because the flock no longer accepts production for that date
  kind: condition
  actor: manager
  entities:
  - entity: flock
    effect: reads
    facts:
    - Archived on
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::history
---

# Refuse adjusting a day of an archived flock

## Trigger

A correction is attempted after the flock was archived.

## Outcome

Nothing changes until the flock is reactivated.
