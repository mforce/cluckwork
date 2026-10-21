---
appliesTo:
- type: entity
  id: bird-movement
  effect: creates
permits:
- actors:
  - owner
  - manager
- actors:
  - worker
  when:
  - fact: Type
    is: Mortality
- related:
  - verb: records
    entity: flock
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: owner
  - verb: approves
    entity: app-connection
  - verb: holds
    entity: ai-agent
  when:
  - fact: Type
    is: Mortality
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Submit daily entries
    is: Allowed
- related:
  - verb: records
    entity: flock
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: manager
  - verb: approves
    entity: app-connection
  - verb: holds
    entity: ai-agent
  when:
  - fact: Type
    is: Mortality
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Submit daily entries
    is: Allowed
- related:
  - verb: records
    entity: flock
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: worker
  - verb: approves
    entity: app-connection
  - verb: holds
    entity: ai-agent
  when:
  - fact: Type
    is: Mortality
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Submit daily entries
    is: Allowed
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/FlockManagement/Flocks/FlockEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/DailyEntryTools.cs
---

# Owners and Managers record bird movements; Workers and connected apps create only mortality, by submitting a day

Culls and recount adjustments are entered by Owners and Managers. A Worker's submitted day appends that day's mortality movement, and so does a day a connected app submits with the Submit daily entries permission while the farm allows connected apps.
