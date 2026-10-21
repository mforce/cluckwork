---
appliesTo:
- type: entity
  id: flock
  effect: reads
permits:
- actors:
  - owner
  - manager
  - sales
  - read-only
- actors:
  - worker
  when:
  - entity: worker
    fact: Flock scope
    is: All flocks
- related:
  - verb: scopes
    entity: flock-assignment
  - verb: holds
    entity: user
  - verb: signs-in-as
    entity: worker
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
- related:
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: read-only
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
  target: src/Cluckwork.Api/Modules/FlockManagement/Flocks/FlockEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Middleware/FlockScopeResolutionMiddleware.cs
- kind: doc
  role: context
  target: docs/designs/388-worker-flock-read-scoping.md
---

# Restricted Workers read only their assigned flocks

Everyone on the farm reads its flocks, except that a Worker holding a flock assignment reads only the flocks assigned, and with them only those flocks' records; any other flock reads as not found rather than forbidden. Owners, Managers, Sales and Read-only people ignore retained assignments. A connected app reads what its person could, with the Read farm data permission, while the farm allows connected apps.
