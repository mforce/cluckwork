---
kind: validation
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the recovery command for a disabled person
  kind: actor
  actor: operator
  entities:
  - entity: user
    effect: reads
    facts: []
  contexts:
    cli:
      place: operator-cli
- text: The Product refuses; the person must be enabled first
  kind: condition
  actor: operator
  entities:
  - entity: user
    effect: reads
    facts: []
  contexts:
    cli:
      place: operator-cli
---

# Refuse recovering a disabled person

## Trigger

A reset would not restore access to a disabled person.

## Outcome

Nothing changes.
