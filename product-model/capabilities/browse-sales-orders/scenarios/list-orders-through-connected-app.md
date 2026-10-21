---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app asks for orders
  kind: actor
  actor: ai-agent
  entities: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product returns pages of orders with their lines and amounts in their currency, and what a confirmed order owes only when the person's role sees money
  kind: product
  actor: ai-agent
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Reference
    - Order date
    - Lines
    - Total
  contexts:
    agent:
      place: farm-mcp
---

# List orders through a connected app

## Trigger

A person's AI assistant reads orders.

## Outcome

The app has the orders the person could read, without discount notes or void reasons.
