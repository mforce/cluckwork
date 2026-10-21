---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app asks for the customer list
  kind: actor
  actor: ai-agent
  entities:
  - entity: customer
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product returns a page of customer names, 50 rows unless more are asked for and at most 100, with where to continue
  kind: product
  actor: ai-agent
  entities:
  - entity: customer
    effect: reads
    facts:
    - Name
  contexts:
    agent:
      place: farm-mcp
---

# List customers through a connected app

## Trigger

A person's AI assistant looks up a customer.

## Outcome

The app has the names without phone, email or address.

## Edge cases

- Not offered when the person is Read-only.
