---
appliesTo:
- type: entity
  id: app-connection
  effect: changes
permits:
- actors:
  - owner
  - manager
  - worker
  - sales
  - read-only
  when:
  - entity: farm
    fact: Connected apps
    is: 'On'
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/OAuth/OAuthEndpoints.cs
---

# Anyone on the farm reapproves their own app while the farm allows connected apps

Reapproval follows the same switch as a first approval.
