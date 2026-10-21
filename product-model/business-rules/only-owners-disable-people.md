---
appliesTo:
- type: entity
  id: user
  effect: changes
  to: Disabled
permits:
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
---

# Only an Owner disables a person

Revoking access is user management.
