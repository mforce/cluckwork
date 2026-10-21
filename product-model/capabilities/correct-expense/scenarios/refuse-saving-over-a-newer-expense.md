---
kind: edge
routes:
  web: Web
steps:
- text: The Manager saves a correction after someone else changed the same expense
  kind: actor
  actor: manager
  entities:
  - entity: expense
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::expenses
- text: The Product refuses with a conflict and the Manager reloads
  kind: condition
  actor: manager
  entities:
  - entity: expense
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::expenses
---

# Refuse a correction made against a stale copy

## Trigger

Two people correct the same expense.

## Outcome

The first save stands.
