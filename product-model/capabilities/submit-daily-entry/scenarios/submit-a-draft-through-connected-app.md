---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app names a Draft day by its id or by flock and date, with a label it picked for this one submission
  kind: actor
  actor: ai-agent
  entities:
  - entity: daily-entry
    effect: reads
    facts: []
  - entity: flock
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product submits the day, creating an egg lot for each graded grade from that flock and day and a mortality bird movement for its deaths
  kind: product
  actor: ai-agent
  entities:
  - entity: daily-entry
    effect: changes
    from: Draft
    to: Submitted
    facts:
    - Condition grades
  - entity: egg-lot
    effect: creates
    facts:
    - Production date
    - Flock
    - Produced
    - Available
  - entity: bird-movement
    effect: creates
    facts:
    - Date
    - Type
    - Quantity
  - entity: flock
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The reply names the egg lots made and says the day is now corrected only with adjust or void on History
  kind: condition
  entities:
  - entity: daily-entry
    effect: reads
    facts: []
  - entity: egg-lot
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
---

# Submit a draft through a connected app

## Trigger

A person's AI assistant finishes a day it recorded for them and submits it.

## Outcome

The day is Submitted and its eggs are in stock, exactly as when a person chooses Submit day in the web app.

## Edge cases

- A day already submitted is refused, and so is one whose grading does not add up to the sellable count.
- Naming the day by flock and date finds exactly one entry or is refused; when several share that flock and date the refusal lists their ids.
- Resending with the same label within 48 hours of a successful submission returns the first answer and changes nothing; the same label with a different day is refused.
- For a Worker with assigned flocks, any other flock reads as not found.
