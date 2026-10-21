---
appliesTo:
- type: entity
  id: inventory-lot
  effect: creates
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
  target: src/Cluckwork.Api/Modules/GeneralInventory/Inventory/InventoryEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/InventoryTools.cs
---

# Owners, Managers, Workers and their connected apps record a purchase

Receiving stock is production work; Sales and Read-only people cannot. A connected app records it with the Record farm operations permission, acting for one of them while the farm allows connected apps.
