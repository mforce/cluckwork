---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app sends a category, a day, a description and an amount, with an optional flock and note and a label it picked for this one recording
  kind: actor
  actor: ai-agent
  entities:
  - entity: expense-category
    effect: reads
    facts: []
  - entity: flock
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product records the expense in the farm's currency and replies with the amount and correct on Expenses as where it is corrected
  kind: product
  actor: ai-agent
  entities:
  - entity: expense
    effect: creates
    facts:
    - Date
    - Description
    - Amount
    - Currency
    - Flock
    - Note
  - entity: expense-category
    effect: reads
    facts: []
  - entity: farm
    effect: reads
    facts:
    - Currency
  - entity: flock
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
---

# Record an expense through a connected app

## Trigger

A person's AI assistant records money the farm spent for them.

## Outcome

The expense counts at once in the period total and the money summary, as when it is added on Expenses.

## Edge cases

- An amount with more decimal places than the farm's currency allows is refused, never rounded; if the farm's currency changes while the expense is recorded, nothing is recorded.
- A deactivated category cannot take new expenses.
- Resending with the same label within 48 hours of a successful recording returns the first answer and records nothing new; the same label with different values is refused.
