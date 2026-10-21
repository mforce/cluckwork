---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses deplete on an active flock and confirms
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
- text: The Product marks the flock Depleted as of the farm's today
  kind: product
  actor: manager
  entities:
  - entity: flock
    effect: changes
    from: Active
    to: Depleted
    facts:
    - Depleted on
  - entity: farm
    effect: reads
    facts:
    - Timezone
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Deplete an active flock

## Trigger

A flock reaches the end of lay.

## Outcome

The flock stays listed as Depleted and accepts only backfill up to its depletion date.
