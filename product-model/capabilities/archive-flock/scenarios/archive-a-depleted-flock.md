---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses archive on a depleted flock and confirms
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
    from: Depleted
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

# Archive a depleted flock

## Trigger

A finished flock no longer needs to appear in pickers.

## Outcome

The flock leaves pickers and the Dashboard and shows on Flocks only behind the archived toggle.
