---
kind: primary
routes:
  cli: Operator CLI
steps:
- text: The Operator runs the suspend command with the farm code and an optional reason
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
- text: The Product suspends the farm and revokes every session
  kind: product
  actor: operator
  entities:
  - entity: farm
    effect: changes
    from: Active
    to: Suspended
    facts: []
  contexts:
    cli:
      place: operator-cli
---

# Suspend an active farm

## Trigger

A farm must be taken offline.

## Outcome

Nobody on the farm can work until it is reactivated; signing out still works.

## Edge cases

- Re-running against an already suspended farm changes nothing and audits nothing.
