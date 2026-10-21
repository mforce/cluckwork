---
kind: primary
routes:
  web: Web
steps:
- text: The Owner removes one of a person's flock assignments and re-enters their own current password
  kind: actor
  actor: owner
  entities:
  - entity: flock-assignment
    effect: reads
    facts:
    - Flock
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product removes the assignment
  kind: product
  actor: owner
  entities:
  - entity: flock-assignment
    effect: removes
  contexts:
    web:
      place: farm-web::setup::users
---

# Remove an assignment

## Trigger

A Worker no longer looks after a flock.

## Outcome

The Worker loses that flock, or regains every flock when it was their last assignment.

## Edge cases

- A promoted person's retained assignments stay visible and can still be removed.
