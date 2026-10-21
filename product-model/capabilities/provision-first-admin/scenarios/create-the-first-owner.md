---
kind: primary
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the bootstrap command with an email
  kind: actor
  actor: operator
  entities:
  - entity: user
    effect: reads
    facts: []
  contexts:
    cli:
      place: operator-cli
- text: The Product creates the default farm's first admin with a one-time password and prints it with the farm code
  kind: product
  actor: operator
  entities:
  - entity: user
    effect: creates
    to: Enabled
    facts:
    - Email
    - Role
    - Password
    - Must change password
  - entity: farm
    effect: reads
    facts:
    - Farm code
  contexts:
    cli:
      place: operator-cli
---

# Create the first Owner

## Trigger

A freshly deployed server has no administrator.

## Outcome

The first Owner can sign in and must replace the password.
