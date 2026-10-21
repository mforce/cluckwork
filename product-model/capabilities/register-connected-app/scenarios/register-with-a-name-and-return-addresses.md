---
kind: primary
routes:
  agent: Agent
steps:
- text: The Connected app registers its name and the addresses the browser should return to
  kind: actor
  actor: ai-agent
  entities: []
  contexts:
    agent:
      place: farm-mcp
- text: The Product records the app, marked as an Unverified app because its details are its own claim
  kind: product
  actor: ai-agent
  entities:
  - entity: ai-agent
    effect: creates
    facts:
    - App name
    - Return addresses
  contexts:
    agent:
      place: farm-mcp
---

# Register with a name and return addresses

## Trigger

An AI assistant is pointed at the farm's MCP address for the first time.

## Outcome

The app can send people to the consent screen; it has no access until someone approves it.

## Edge cases

- Registering works whether or not the farm allows connected apps.
- An app nobody approves is removed after a while.
