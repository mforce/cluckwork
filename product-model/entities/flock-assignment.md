---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Farm/Accounts/UserRoleAssignment.cs
---

# Flock assignment

An entry that grants a Worker scope over one flock. A Worker with no assignment, or a farm-wide one, works with every flock; the first flock assignment restricts them.

## Information kept

- **Flock** — the flock the Worker may work with, or farm-wide
