---
appliesTo:
- type: capability
  id: create-user
- type: capability
  id: reset-user-password
- type: capability
  id: change-user-role
- type: capability
  id: change-user-email
- type: capability
  id: disable-user
- type: capability
  id: enable-user
- type: capability
  id: assign-flock
- type: capability
  id: unassign-flock
- type: capability
  id: connect-app
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/StepUpGrantService.cs
- kind: doc
  role: context
  target: docs/plans/606-step-up-flock-scope/01-threat-model.md
---

# Changing who can sign in or what they may do requires re-entering one's current password

Creating a person, resetting a password, changing a role or login email, disabling or enabling a person, assigning or removing a flock, and approving a connected app each spend a single-use proof of the acting person's current password that lasts five minutes. Every refusal reason gives the same denial. Editing a display name needs no proof.

## Rationale

A stolen but still valid session must not be enough to create or widen durable access.
