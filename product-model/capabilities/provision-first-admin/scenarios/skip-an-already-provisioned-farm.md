---
kind: alternative
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the bootstrap command again
  kind: actor
  actor: operator
  entities: []
  contexts:
    cli:
      place: operator-cli
- text: The default farm already has an admin, so nothing is created and no password is printed
  kind: condition
  actor: operator
  entities:
  - entity: user
    effect: reads
    facts:
    - Role
  - entity: farm
    effect: reads
    facts: []
  contexts:
    cli:
      place: operator-cli
---

# Do nothing when an Owner exists

## Trigger

The command is re-run.

## Outcome

Nothing changes.
