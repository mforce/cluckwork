---
kind: primary
routes:
  web: Web
steps:
- text: The Owner chooses password on another person, enters a new password and re-enters their own current password
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
- text: The Product sets the new password, clears any pending requirement to change it and ends every session of that person
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
      place: farm-web::setup::users
---

# Set a colleague's password

## Trigger

A colleague has forgotten their password.

## Outcome

The colleague signs in with the new password; their old sessions stop on their next request.
