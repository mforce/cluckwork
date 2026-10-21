---
kind: alternative
routes:
  web: Web
steps:
- text: The Owner enters the printed one-time password as the current one and a new password
  kind: actor
  actor: owner
  entities: []
  contexts:
    web:
      place: farm-web::farm::set-password
- text: The Product replaces the password and clears the requirement to change it
  kind: product
  actor: owner
  entities:
  - entity: user
    effect: changes
    facts:
    - Password
    - Must change password
  contexts:
    web:
      place: farm-web::farm::set-password
---

# Replace a one-time password

## Trigger

A newly provisioned Owner is held at Set your password.

## Outcome

The whole app opens.
