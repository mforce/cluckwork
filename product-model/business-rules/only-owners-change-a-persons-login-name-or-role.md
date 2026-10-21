---
appliesTo:
- type: entity
  id: user
  effect: changes
  facts:
  - Email
  - Display name
  - Role
permits:
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
---

# Only an Owner changes a person's email, display name or role

User management is the Owner's.
