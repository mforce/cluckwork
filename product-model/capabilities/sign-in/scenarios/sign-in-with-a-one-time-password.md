---
kind: alternative
routes:
  web: Web
steps:
- text: The Visitor signs in with a printed one-time password
  kind: actor
  actor: visitor
  entities:
  - entity: farm
    effect: reads
    facts: []
  - entity: user
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::public::sign-in
- text: The person must change the password, so Set your password replaces the whole app
  kind: condition
  actor: visitor
  entities:
  - entity: user
    effect: reads
    facts:
    - Must change password
  contexts:
    web:
      place: farm-web::public::sign-in
---

# Sign in with a one-time password

## Trigger

A newly provisioned Owner signs in for the first time.

## Outcome

They are signed in but can only replace the password or sign out.
