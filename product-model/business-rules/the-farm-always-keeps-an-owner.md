---
appliesTo:
- type: capability
  id: change-user-role
- type: capability
  id: disable-user
- type: capability
  id: reset-user-password
- type: capability
  id: change-user-email
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Users/ChangeUserRole/ChangeUserRoleHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Users/DisableUser/DisableUserHandler.cs
---

# Nobody targets themselves, and the farm always keeps an active Owner

An Owner cannot change their own role, reset their own password from Users, or disable themselves; the last active Owner cannot be demoted or disabled; and an Owner changes their own login email only while another active Owner remains.
