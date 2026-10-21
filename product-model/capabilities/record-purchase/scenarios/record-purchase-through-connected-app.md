---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app sends an item, the received date and the quantity, with an optional unit cost, supplier lot number and expiry and a label it picked for this one recording
  kind: actor
  actor: ai-agent
  entities:
  - entity: inventory-item
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product creates a lot for the batch and the item's stock on hand rises
  kind: product
  actor: ai-agent
  entities:
  - entity: inventory-lot
    effect: creates
    facts:
    - Received date
    - Quantity received
    - Remaining
    - Unit cost
    - Supplier lot number
    - Expiry
  - entity: inventory-item
    effect: reads
    facts:
    - Default unit cost
  contexts:
    agent:
      place: farm-mcp
- text: The reply carries the lot's quantity and unit cost and names Correct stock on Inventory as where the lot is corrected or written off
  kind: condition
  entities:
  - entity: inventory-lot
    effect: reads
    facts:
    - Quantity received
    - Unit cost
  contexts:
    agent:
      place: farm-mcp
---

# Record a purchase through a connected app

## Trigger

A person's AI assistant records stock that arrived for them.

## Outcome

The item has a new lot that counts at once, as when a purchase is recorded on Inventory.

## Edge cases

- Without a unit cost the item's default unit cost is used, and an item with neither is refused.
- A unit cost with more decimal places than the farm's currency allows is refused, never rounded; if the farm's currency changes while the purchase is recorded, nothing is recorded.
- Purchases are farm-wide, so a Worker's flock assignments do not narrow them.
- Resending with the same label within 48 hours of a successful recording returns the first answer and records nothing new; the same label with different values is refused.
