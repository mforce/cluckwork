---
availability:
- place: operator-cli
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/RecoverAdminCliCommand.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/AdminRecoveryService.cs
- kind: doc
  role: context
  target: docs/decisions/265-break-glass-recovery.md
---

# Recover an admin

Break-glass recovery for a sole Owner who lost their password: reset it to a printed temporary password, clear any lockout and revoke every session. It is audited as a break-glass reset with the reason and host.
