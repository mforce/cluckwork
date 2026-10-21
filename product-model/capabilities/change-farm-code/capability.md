---
availability:
- place: operator-cli
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/RenameAccountCliCommand.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/AccountRenameService.cs
- kind: doc
  role: context
  target: docs/decisions/732-farm-code-rename.md
---

# Change a farm code

Give a farm a different farm code. The old code is immediately reusable; there is no endpoint or setting for it in the app.
