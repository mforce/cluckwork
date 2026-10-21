---
availability:
- place: operator-cli
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/SuspendAccountCliCommand.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/AccountSuspensionService.cs
---

# Suspend a farm

Take a farm offline immediately: its people are refused at sign-in and on their next request, and every session is revoked. Nothing is deleted.
