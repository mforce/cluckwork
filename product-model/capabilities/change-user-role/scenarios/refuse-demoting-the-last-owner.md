---
kind: validation
routes:
  web: Web
steps:
- text: The Owner changes their own role, or the role of the only other active Owner
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
- text: 'The Product refuses: nobody changes their own role, and the last active Owner cannot be demoted'
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

# Refuse demoting the last Owner

## Trigger

A change would lock the farm out of user administration.

## Outcome

The role is unchanged.
