---
kind: validation
routes:
  web: Web
steps:
- text: The Worker records a day for an archived flock, or for a depleted flock after its depletion date
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
- text: The Product refuses the day because the flock does not accept production on that date
  kind: condition
  actor: worker
  entities:
  - entity: flock
    effect: reads
    facts:
    - Depleted on
    - Archived on
  contexts:
    web:
      place: farm-web::farm::daily-entry
---

# Refuse a day the flock no longer accepts

## Trigger

A day is recorded outside the flock's capture window.

## Outcome

Nothing is saved. A depleted flock still accepts backfill on or before its depletion date.

## Edge cases

- A day after the farm's today is refused outright, judged in the farm's timezone.
