---
kind: alternative
routes:
  web: Web
steps:
- text: The Manager chooses archive on an active flock and confirms
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
- text: The Product archives the flock as of the farm's today
  kind: product
  actor: manager
  entities:
  - entity: flock
    effect: changes
    from: Active
    to: Archived
    facts:
    - Archived on
  - entity: farm
    effect: reads
    facts:
    - Timezone
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Archive a flock created by mistake

## Trigger

A flock was created that should not exist.

## Outcome

The flock is archived without ever being depleted.
