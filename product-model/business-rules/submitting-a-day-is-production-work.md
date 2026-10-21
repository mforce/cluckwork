---
appliesTo:
- type: entity
  id: daily-entry
  effect: changes
  to: Submitted
permits:
- actors:
  - owner
  - manager
- actors:
  - worker
  when:
  - entity: worker
    fact: Flock scope
    is: All flocks
- related:
  - verb: has
    entity: flock
  - verb: scopes
    entity: flock-assignment
  - verb: holds
    entity: user
  - verb: signs-in-as
    entity: worker
- related:
  - verb: has
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
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Submit daily entries
    is: Allowed
- related:
  - verb: has
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
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Submit daily entries
    is: Allowed
- related:
  - verb: has
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
  target: src/Cluckwork.Api/Modules/EggOperations/DailyEntries/DailyEntryEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/DailyEntryTools.cs
---

# Owners, Managers, Workers within their flock scope, and their connected apps submit a daily entry

A daily entry is submitted by an Owner, a Manager, a Worker for a flock within their scope, or a connected app with the Submit daily entries permission acting for one of them while the farm allows connected apps.
