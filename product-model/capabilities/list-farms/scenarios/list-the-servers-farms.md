---
kind: primary
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the list command
  kind: actor
  actor: operator
  entities: []
  contexts:
    cli:
      place: operator-cli
- text: The Product prints each farm's code and name and whether it is active
  kind: product
  actor: operator
  entities:
  - entity: farm
    effect: reads
    facts:
    - Farm code
    - Name
  contexts:
    cli:
      place: operator-cli
---

# List the server's farms

## Trigger

An operator needs a farm code.

## Outcome

They have the list.
