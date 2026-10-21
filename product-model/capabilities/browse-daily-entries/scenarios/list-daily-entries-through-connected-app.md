---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app asks for daily entries
  kind: actor
  actor: ai-agent
  entities: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product returns a page of daily entries within the person's scope, 50 rows unless more are asked for and at most 100, with where to continue
  kind: product
  actor: ai-agent
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Date
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Mortality
    - Grading
  contexts:
    agent:
      place: farm-mcp
---

# List daily entries through a connected app

## Trigger

A person's AI assistant reads recorded days.

## Outcome

The app has the entries the person could read, without any reason text a person typed.
