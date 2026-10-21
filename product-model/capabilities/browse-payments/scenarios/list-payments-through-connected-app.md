---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app asks for one order's payments
  kind: actor
  actor: ai-agent
  entities:
  - entity: payment
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product returns the payments with their dates, amounts and methods in the order's currency, without references or notes
  kind: product
  actor: ai-agent
  entities:
  - entity: payment
    effect: reads
    facts:
    - Date
    - Amount
    - Method
  contexts:
    agent:
      place: farm-mcp
---

# List an order's payments through a connected app

## Trigger

An Owner, Manager or Sales user's assistant checks an order's settlement.

## Outcome

The app has the payments without typed text.
