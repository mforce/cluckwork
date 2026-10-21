---
kind: validation
routes:
  web: Web
steps:
- text: The Owner chooses password on their own row
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Email
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product refuses; Owners change their own password on Account by proving the current one
  kind: condition
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
---

# Refuse resetting one's own password this way

## Trigger

Skipping the current-password proof for oneself would turn a stolen session into a takeover.

## Outcome

Nothing changes.
