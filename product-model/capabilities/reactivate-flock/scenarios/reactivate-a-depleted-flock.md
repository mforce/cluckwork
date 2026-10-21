---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses reactivate on a depleted flock
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
- text: The Product returns the flock to Active and clears its depletion date
  kind: product
  actor: manager
  entities:
  - entity: flock
    effect: changes
    from: Depleted
    to: Active
    facts:
    - Depleted on
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Reactivate a depleted flock

## Trigger

A flock was depleted too early.

## Outcome

The flock accepts capture for any date again.
