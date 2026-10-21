---
appliesTo:
- type: entity
  id: farm
  effect: changes
  facts:
  - Farm code
permits:
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/RenameAccountCliCommand.cs
---

# Only an operator changes a farm code

There is no endpoint and no setting for it, so a farm cannot rename itself.
