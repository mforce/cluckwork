---
kind: validation
routes:
  web: Web
steps:
- text: The Visitor signs in with an unknown email or a wrong password
  kind: actor
  actor: visitor
  entities:
  - entity: user
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::public::sign-in
- text: The Product answers Invalid email or password without saying which, or whether the account exists
  kind: condition
  actor: visitor
  entities:
  - entity: user
    effect: reads
    facts:
    - Email
  contexts:
    web:
      place: farm-web::public::sign-in
---

# Refuse wrong credentials

## Trigger

The credentials do not match.

## Outcome

Nobody is signed in.

## Edge cases

- After five failed attempts the account locks for fifteen minutes and the same answer is given, even for the right password.
- Too many sign-in attempts from one address are refused with Too many sign-in attempts.
