---
kind: primary
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the provision command with a farm code, a name and the first admin's email
  kind: actor
  actor: operator
  entities:
  - entity: farm
    effect: reads
    facts: []
  - entity: user
    effect: reads
    facts: []
  contexts:
    cli:
      place: operator-cli
- text: The Product echoes the normalized farm code, then creates the farm with its standard grades, packed units and first admin, and prints the one-time password
  kind: product
  actor: operator
  entities:
  - entity: farm
    effect: creates
    to: Active
    facts:
    - Name
    - Farm code
    - Timezone
    - Currency
    - Connected apps
  - entity: egg-grade
    effect: creates
    to: Active
    facts:
    - Name
    - Grade type
    - Sort order
    - Saleable
    - Condition counter
    with: farm
  - entity: packed-unit
    effect: creates
    facts:
    - Unit
    - Eggs per unit
    - Active
    with: farm
  - entity: user
    effect: creates
    to: Enabled
    facts:
    - Email
    - Role
    - Password
    - Must change password
    with: farm
  contexts:
    cli:
      place: operator-cli
---

# Provision a new farm

## Trigger

A new farm joins the server.

## Outcome

The first Owner signs in with the farm code and the one-time password and must replace it.
