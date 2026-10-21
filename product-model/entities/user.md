---
relations:
- entity: flock-assignment
  verb: holds
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/ApplicationUser.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Farm/Contracts/Roles.cs
---

# User

A person's sign-in on one farm, whatever their role. The same email address can belong to separate users on different farms.

## Information kept

- **Email** — the login email used with the farm code; unique within the farm
- **Display name** — an optional name shown instead of the email
- **Role** — Admin (Owner), Manager, Worker, Sales or Read-only
- **Password** — the secret the person signs in with; never shown
- **Must change password** — set on a one-time password; until cleared the person can only replace their password or sign out
- **UI language** — English, Spanish or Tagalog, or the app default
- **Counting unit** — the person's own Daily entry stepper unit, overriding the farm default

## States

### Enabled

The person can sign in.

### Disabled

Every session ends on its next request and the person cannot sign in; nothing about them is deleted.
