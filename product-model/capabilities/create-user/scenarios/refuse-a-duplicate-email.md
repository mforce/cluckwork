---
kind: validation
routes:
  web: Web
steps:
- text: The Owner creates a person with an email someone else already signs in with
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Email
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product refuses the new person
  kind: condition
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Email
  contexts:
    web:
      place: farm-web::setup::users
---

# Refuse an email already on the farm

## Trigger

Two people would share a login.

## Outcome

Nobody is created.

## Edge cases

- A wrong current password is refused with the same denial whatever the reason, and nothing is created.
