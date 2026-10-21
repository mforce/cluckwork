---
availability:
- place: operator-cli
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/ReactivateAccountCliCommand.cs
---

# Reactivate a farm

Bring a suspended farm back exactly as it was; sessions from before the suspension stay dead, so everyone signs in again.
