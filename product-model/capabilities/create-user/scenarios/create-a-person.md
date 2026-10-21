---
kind: primary
routes:
  web: Web
steps:
- text: The Owner enters the person's email, password, display name and role, and re-enters their own current password
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product creates the person, enabled
  kind: product
  actor: owner
  entities:
  - entity: user
    effect: creates
    to: Enabled
    facts:
    - Email
    - Display name
    - Role
    - Password
  contexts:
    web:
      place: farm-web::setup::users
---

# Create a person

## Trigger

Someone new starts working on the farm.

## Outcome

They can sign in with the farm code, their email and the password.
