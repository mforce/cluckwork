---
kind: primary
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the rename command with the current and the new farm code
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
- text: The Product changes the farm code
  kind: product
  actor: operator
  entities:
  - entity: farm
    effect: changes
    facts:
    - Farm code
  contexts:
    cli:
      place: operator-cli
---

# Change a farm's code

## Trigger

A farm code was chosen wrongly.

## Outcome

People sign in with the new code.

## Edge cases

- A reserved word or a code another farm uses is refused.
