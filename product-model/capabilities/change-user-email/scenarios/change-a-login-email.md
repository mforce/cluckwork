---
kind: primary
routes:
  web: Web
steps:
- text: The Owner chooses change email on a person, enters the new address and re-enters their own current password
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
- text: The Product replaces the login email and ends the person's sessions
  kind: product
  actor: owner
  entities:
  - entity: user
    effect: changes
    facts:
    - Email
  contexts:
    web:
      place: farm-web::setup::users
---

# Change a login email

## Trigger

A person's email address changes.

## Outcome

They sign in with the new address from now on.

## Edge cases

- An Owner may change their own address only while another active Owner remains.
- An address someone else on the farm already uses is refused.
