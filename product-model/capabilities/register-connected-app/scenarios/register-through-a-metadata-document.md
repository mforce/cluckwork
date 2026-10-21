---
kind: alternative
routes:
  agent: Agent
steps:
- text: The Connected app identifies itself with the https address of a client metadata document it publishes
  kind: actor
  actor: ai-agent
  entities: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product fetches the document itself and takes the app's name and return addresses from it, with that website as its verified domain
  kind: product
  actor: ai-agent
  entities:
  - entity: ai-agent
    effect: creates
    facts:
    - App name
    - Return addresses
    - Verified domain
  contexts:
    agent:
      place: farm-mcp
---

# Identify with a client metadata document

## Trigger

An app publishes its details on its own website.

## Outcome

The consent screen shows the app's verified domain; its name remains its own choice.
