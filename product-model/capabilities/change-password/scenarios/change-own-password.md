---
kind: primary
routes:
  web: Web
steps:
- text: The Worker enters their current password and a new one on Account
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::account
- text: The Product replaces the password and ends every other session of this person
  kind: product
  actor: worker
  entities:
  - entity: user
    effect: changes
    facts:
    - Password
  contexts:
    web:
      place: farm-web::farm::account
---

# Change one's own password

## Trigger

Someone wants a new password.

## Outcome

Their other devices must sign in again; this one stays signed in.
