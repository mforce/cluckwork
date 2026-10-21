---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app asks for egg lots, then for one lot's movements
  kind: actor
  actor: ai-agent
  entities:
  - entity: egg-lot
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product returns pages of lots with the link to the day that created each and any restriction, and the lot's movements without typed reasons
  kind: product
  actor: ai-agent
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Production date
    - Flock
    - Produced
    - Available
    - Restricted until
  - entity: egg-movement
    effect: reads
    facts:
    - When
    - Type
    - Change
  contexts:
    agent:
      place: farm-mcp
---

# List egg lots through a connected app

## Trigger

A person's AI assistant traces stock.

## Outcome

The app has the lots and movements the person could read.
