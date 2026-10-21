---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app sends a flock, a day and the source with a quantity or meter readings, with a label it picked for this one recording
  kind: actor
  actor: ai-agent
  entities:
  - entity: flock
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product records the water usage with any same-day daily entry link
  kind: product
  actor: ai-agent
  entities:
  - entity: water-usage
    effect: creates
    facts:
    - Date
    - Quantity
    - Unit
    - Source
    - Meter readings
    - Note
    - Daily entry link
  - entity: flock
    effect: reads
    facts: []
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The reply carries the quantity and unit recorded and names correct on Water as where it is corrected
  kind: condition
  entities:
  - entity: water-usage
    effect: reads
    facts:
    - Quantity
    - Unit
  contexts:
    agent:
      place: farm-mcp
---

# Record water through a connected app

## Trigger

A person's AI assistant records a flock's water for them.

## Outcome

The usage counts at once, as when it is logged on Water.

## Edge cases

- Without a unit the quantity is in litres, and meter readings that disagree with a quantity are refused, as they are in the web app.
- Resending with the same label within 48 hours of a successful recording returns the first answer and records nothing new; the same label with different values is refused.
- For a Worker with assigned flocks, any other flock reads as not found.
