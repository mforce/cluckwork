---
kind: primary
routes:
  web: Web
steps:
- text: The Manager opens the audit log and filters it by record type, action and dates
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::setup::audit-log
- text: The Product lists the matching events with who acted, through which connected app, what was done to which record and the reason
  kind: product
  actor: manager
  entities:
  - entity: audit-event
    effect: reads
    facts:
    - Actor
    - When
    - Action
    - Record
    - Reason
    - Details
    - Connected app
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::audit-log
---

# Browse the farm's audit events

## Trigger

An Owner or Manager investigates what happened.

## Outcome

They see the events in order.

## Edge cases

- Clear filters resets every filter but keeps a record scope opened from an Audit history link.
- Egg lots offer an Adjustment history link instead, scoped to their write-offs and recounts.
