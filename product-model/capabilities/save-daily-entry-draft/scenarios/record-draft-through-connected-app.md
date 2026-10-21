---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app sends one flock's counts for a day, with a label it picked for this one recording
  kind: actor
  actor: ai-agent
  entities:
  - entity: flock
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product records the day as a Draft, replacing the numbers of an existing draft for that flock and day
  kind: product
  actor: ai-agent
  entities:
  - entity: daily-entry
    effect: creates
    to: Draft
    facts:
    - Date
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Mortality
    - Grading
  - entity: flock
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
- text: The eggs reach stock only once the draft is submitted, in the web app or by an app allowed to submit it
  kind: condition
  entities:
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    agent:
      place: farm-mcp
---

# Record a draft through a connected app

## Trigger

A person's AI assistant records the day's numbers for them.

## Outcome

The day is a Draft that the person or a colleague finishes and submits in the web app, or that an app with the Submit daily entries permission submits.

## Edge cases

- Resending with the same label within 48 hours of a successful recording returns the first answer and records nothing new; the same label with different numbers is refused.
- A day already submitted cannot be changed through the app.
- A flock name several flocks share is refused with each flock's id, and the app then uses the id.
- For a Worker with assigned flocks, any other flock reads as not found.
