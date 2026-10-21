---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app asks what each customer owes
  kind: actor
  actor: ai-agent
  entities:
  - entity: customer
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product returns each customer's outstanding balance with its currency
  kind: product
  actor: ai-agent
  entities:
  - entity: customer
    effect: reads
    facts:
    - Name
    - Outstanding balance
  contexts:
    agent:
      place: farm-mcp
---

# Read what customers owe through a connected app

## Trigger

An Owner, Manager or Sales user's assistant chases payment.

## Outcome

The app has the balances the person could read.
