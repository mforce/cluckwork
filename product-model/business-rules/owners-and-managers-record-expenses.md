---
appliesTo:
- type: entity
  id: expense
  effect: creates
permits:
- actors:
  - owner
  - manager
- related:
  - verb: classifies
    entity: expense-category
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
    fact: Record expenses
    is: Allowed
- related:
  - verb: classifies
    entity: expense-category
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
    fact: Record expenses
    is: Allowed
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Endpoints/CluckworkEndpointGroups.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/ExpenseTools.cs
---

# Owners, Managers and their connected apps record expenses

An expense is recorded by an Owner or a Manager, or by a connected app with the Record expenses permission acting for one of them while the farm allows connected apps. Workers, Sales and Read-only people never record expenses.
