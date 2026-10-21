---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app asks for stock
  kind: actor
  actor: ai-agent
  entities: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product returns available and restricted eggs per grade
  kind: product
  actor: ai-agent
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Available
    - Restricted until
  - entity: egg-grade
    effect: reads
    facts:
    - Name
  contexts:
    agent:
      place: farm-mcp
---

# Read stock through a connected app

## Trigger

A person's AI assistant checks stock.

## Outcome

The app has the same per-grade stock the Stock screen shows.
