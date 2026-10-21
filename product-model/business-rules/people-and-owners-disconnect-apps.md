---
appliesTo:
- type: entity
  id: app-connection
  effect: removes
permits:
- related:
  - verb: approves
    entity: owner
- related:
  - verb: approves
    entity: manager
- related:
  - verb: approves
    entity: worker
- related:
  - verb: approves
    entity: sales
- related:
  - verb: approves
    entity: read-only
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Me/MeEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
---

# A person disconnects their own apps; an Owner disconnects anyone's

Disconnecting revokes the person's approvals and tokens for that app; the person keeps their sign-in.
