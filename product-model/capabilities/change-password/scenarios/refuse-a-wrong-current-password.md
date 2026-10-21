---
kind: validation
routes:
  web: Web
steps:
- text: The Worker enters a current password that is wrong
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::account
- text: The Product refuses and the password is unchanged
  kind: condition
  actor: worker
  entities:
  - entity: user
    effect: reads
    facts:
    - Password
  contexts:
    web:
      place: farm-web::farm::account
---

# Refuse a wrong current password

## Trigger

The current password is not proven.

## Outcome

Nothing changes.

## Edge cases

- A new password that breaks the password rules is refused.
