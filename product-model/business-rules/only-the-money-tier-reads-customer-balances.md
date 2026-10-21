---
appliesTo:
- type: entity
  id: customer
  effect: reads
  facts:
  - Outstanding balance
permits:
- actors:
  - owner
  - manager
  - sales
- related:
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
    fact: Read farm data
    is: Allowed
- related:
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
    fact: Read farm data
    is: Allowed
- related:
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: sales
  - verb: approves
    entity: app-connection
  - verb: holds
    entity: ai-agent
  when:
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Read farm data
    is: Allowed
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/PaymentEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/CommerceTools.cs
---

# Only Owners, Managers and Sales read what a customer owes

A customer's outstanding balance is money; a connected app reads it only for an Owner, Manager or Sales user. The Customers screen shows its column to Owners and Managers only.
