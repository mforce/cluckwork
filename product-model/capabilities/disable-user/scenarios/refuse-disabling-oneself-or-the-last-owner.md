---
kind: validation
routes:
  web: Web
steps:
- text: The Owner chooses disable on their own row or on the last active Owner
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Role
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product refuses
  kind: condition
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Role
  contexts:
    web:
      place: farm-web::setup::users
---

# Refuse disabling oneself or the last Owner

## Trigger

The change would lock someone out of their own administration.

## Outcome

Nothing changes.
