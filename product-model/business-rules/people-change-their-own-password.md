---
appliesTo:
- type: entity
  id: user
  effect: changes
  facts:
  - Password
  - Must change password
permits:
- related:
  - verb: signs-in-as
    entity: owner
- related:
  - verb: signs-in-as
    entity: manager
- related:
  - verb: signs-in-as
    entity: worker
- related:
  - verb: signs-in-as
    entity: sales
- related:
  - verb: signs-in-as
    entity: read-only
- actors:
  - owner
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Auth/AuthEndpoints.Password.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/RecoverAdminCliCommand.cs
---

# A person changes their own password; an Owner or an operator resets someone else's

Self-service needs the current password. An Owner sets any other person's password, and an operator's break-glass reset recovers a locked-out Owner.
