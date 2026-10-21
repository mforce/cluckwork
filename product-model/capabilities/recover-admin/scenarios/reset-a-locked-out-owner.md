---
kind: primary
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the recovery command for the locked-out admin's farm code and email with a reason
  kind: actor
  actor: operator
  entities:
  - entity: farm
    effect: reads
    facts:
    - Farm code
  - entity: user
    effect: reads
    facts:
    - Email
  contexts:
    cli:
      place: operator-cli
- text: The Product resets the password to a temporary one that must be changed at sign-in, clears the lockout, ends every session and prints the password
  kind: product
  actor: operator
  entities:
  - entity: user
    effect: changes
    facts:
    - Password
    - Must change password
  - entity: farm
    effect: reads
    facts: []
  contexts:
    cli:
      place: operator-cli
---

# Reset a locked-out Owner

## Trigger

A sole Owner is locked out and nobody else can reset them.

## Outcome

The Owner signs in with the temporary password and must replace it.
