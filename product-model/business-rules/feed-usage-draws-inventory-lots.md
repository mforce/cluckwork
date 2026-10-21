---
appliesTo:
- type: entity
  id: inventory-lot
  effect: changes
  contexts:
  - place: farm-web::farm::feed
  - place: farm-mcp
permits:
- actors:
  - owner
  - manager
  - worker
- related:
  - verb: has
    entity: inventory-item
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
  - verb: has
    entity: inventory-item
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
  - verb: has
    entity: inventory-item
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
  target: src/Cluckwork.Application/Modules/GeneralInventory/Inventory/RecordFeedUsage/RecordFeedUsageHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/InventoryTools.cs
---

# Owners, Managers, Workers and their connected apps draw inventory lots down by recording feed

Recording feed usage consumes the item's lots oldest first; it is production work, which a connected app does with the Record farm operations permission.
