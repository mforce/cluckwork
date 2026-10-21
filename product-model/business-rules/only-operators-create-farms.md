---
appliesTo:
- type: entity
  id: farm
  effect: creates
permits:
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/ProvisionAccountCliCommand.cs
---

# Only an operator creates a farm

Farms are provisioned from the server, never from the app.
