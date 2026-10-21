---
appliesTo:
- type: entity
  id: water-usage
  effect: creates
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
  - verb: consumes
    entity: flock
  - verb: scopes
    entity: flock-assignment
  - verb: holds
    entity: user
  - verb: signs-in-as
    entity: worker
- related:
  - verb: consumes
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
    fact: Record farm operations
    is: Allowed
- related:
  - verb: consumes
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
    fact: Record farm operations
    is: Allowed
- related:
  - verb: consumes
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
    fact: Record farm operations
    is: Allowed
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/GeneralInventory/Water/WaterUsageEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/InventoryTools.cs
---

# Owners, Managers, Workers within their flock scope, and their connected apps record water usage

Recording water is production work. A connected app records it with the Record farm operations permission, acting for one of them while the farm allows connected apps.
