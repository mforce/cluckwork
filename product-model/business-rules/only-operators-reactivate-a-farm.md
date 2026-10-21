---
appliesTo:
- type: entity
  id: farm
  effect: changes
  to: Active
permits:
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/ReactivateAccountCliCommand.cs
---

# Only an operator reactivates a farm

Reactivation is run at a shell on the deployment.
