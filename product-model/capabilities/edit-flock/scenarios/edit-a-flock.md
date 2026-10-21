---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses edit on a flock and changes its details
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
- text: The Product saves the flock's new details
  kind: product
  actor: manager
  entities:
  - entity: flock
    effect: changes
    facts:
    - Name
    - Breed
    - Placement date
    - Initial count
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Edit a flock

## Trigger

A flock's details were entered wrongly.

## Outcome

The flock shows its corrected details everywhere, history included.

## Edge cases

- Someone saving against a stale copy of the flock is refused and reloads.
