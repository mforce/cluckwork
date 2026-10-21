---
appliesTo:
- type: entity
  id: user
  effect: creates
permits:
- actors:
  - owner
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
---

# Only Owners and operators create a person

Owners add people on the Users screen; operators create a farm's first Owner from the server.
