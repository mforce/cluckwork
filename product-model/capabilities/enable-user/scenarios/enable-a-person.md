---
kind: primary
routes:
  web: Web
steps:
- text: The Owner chooses enable on a disabled person and re-enters their own current password
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
- text: The Product enables the person
  kind: product
  actor: owner
  entities:
  - entity: user
    effect: changes
    from: Disabled
    to: Enabled
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
---

# Enable a person

## Trigger

A disabled person returns.

## Outcome

They can sign in again with their password.
