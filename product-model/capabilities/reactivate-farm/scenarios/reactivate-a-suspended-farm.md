---
kind: primary
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the reactivate command with the farm code
  kind: actor
  actor: operator
  entities:
  - entity: farm
    effect: reads
    facts:
    - Farm code
  contexts:
    cli:
      place: operator-cli
- text: The Product reactivates the farm
  kind: product
  actor: operator
  entities:
  - entity: farm
    effect: changes
    from: Suspended
    to: Active
    facts: []
  contexts:
    cli:
      place: operator-cli
---

# Reactivate a suspended farm

## Trigger

A suspended farm comes back online.

## Outcome

Its people sign in again and find everything as it was.
