---
appliesTo:
- type: entity
  id: farm
  effect: changes
  to: Suspended
permits:
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/SuspendAccountCliCommand.cs
---

# Only an operator suspends a farm

Suspension is run at a shell on the deployment.
