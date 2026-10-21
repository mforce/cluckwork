---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app sends a flock, an item, a day and a quantity, with a label it picked for this one recording
  kind: actor
  actor: ai-agent
  entities:
  - entity: flock
    effect: reads
    facts: []
  - entity: inventory-item
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product draws the quantity from the item's oldest lots received by that day and records the usage with its estimated cost and any same-day daily entry link
  kind: product
  actor: ai-agent
  entities:
  - entity: inventory-lot
    effect: changes
    facts:
    - Remaining
  - entity: feed-usage
    effect: creates
    facts:
    - Date
    - Quantity
    - Estimated cost
    - Note
    - Daily entry link
  - entity: inventory-item
    effect: reads
    facts: []
  - entity: flock
    effect: reads
    facts: []
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The reply carries the estimated cost and names Correct stock on Inventory as where the stock is corrected
  kind: condition
  entities:
  - entity: feed-usage
    effect: reads
    facts:
    - Estimated cost
  contexts:
    agent:
      place: farm-mcp
---

# Record feed through a connected app

## Trigger

A person's AI assistant records what a flock ate for them.

## Outcome

The usage counts at once, as when it is logged on Feed, and the item's stock on hand falls.

## Edge cases

- A quantity beyond the stock received by that day is refused, as it is in the web app.
- Resending with the same label within 48 hours of a successful recording returns the first answer and records nothing new; the same label with different values is refused.
- For a Worker with assigned flocks, any other flock reads as not found.
