---
kind: validation
routes:
  cli: Operator CLI
steps:
- text: The Operator provisions a farm with a reserved word such as admin or a code another farm uses
  kind: actor
  actor: operator
  entities:
  - entity: farm
    effect: reads
    facts: []
  contexts:
    cli:
      place: operator-cli
- text: The Product refuses and creates nothing
  kind: condition
  actor: operator
  entities:
  - entity: farm
    effect: reads
    facts:
    - Farm code
  contexts:
    cli:
      place: operator-cli
---

# Refuse a reserved or taken farm code

## Trigger

The farm code cannot be used.

## Outcome

No farm is created.
