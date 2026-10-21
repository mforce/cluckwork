---
kind: primary
routes:
  web: Web
steps:
- text: The Owner chooses edit on a person and changes their display name
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Display name
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product saves the display name
  kind: product
  actor: owner
  entities:
  - entity: user
    effect: changes
    facts:
    - Display name
  contexts:
    web:
      place: farm-web::setup::users
---

# Rename a person

## Trigger

A person's name should show instead of their email.

## Outcome

Lists show the name; past audit history keeps the email it recorded.
