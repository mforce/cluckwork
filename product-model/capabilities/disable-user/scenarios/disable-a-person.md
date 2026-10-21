---
kind: primary
routes:
  web: Web
steps:
- text: The Owner chooses disable on another person and re-enters their own current password
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
- text: The Product disables the person and ends all their sessions
  kind: product
  actor: owner
  entities:
  - entity: user
    effect: changes
    from: Enabled
    to: Disabled
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
---

# Disable a person

## Trigger

Someone leaves the farm.

## Outcome

They can no longer sign in, and everything that names them stays as it was.
