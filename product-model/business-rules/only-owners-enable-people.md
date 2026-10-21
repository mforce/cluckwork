---
appliesTo:
- type: entity
  id: user
  effect: changes
  to: Enabled
permits:
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
---

# Only an Owner enables a person

Restoring access is user management.
