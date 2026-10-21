---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Auditing/AuditEvent.cs
- kind: doc
  role: context
  target: docs/decisions/500-audit-actor.md
---

# Audit event

One entry of the farm's append-only audit log: every corrective, destructive or configuration change, with who made it and through which connected app.

## Information kept

- **Actor** — the person's email as it was, or a named system actor for an operator verb
- **When** — the UTC time of the change
- **Action** — what was done, such as Daily entry adjusted
- **Record** — the record type and record the change was made to
- **Reason** — the reason the change carried, if any
- **Details** — a small summary of what changed
- **Connected app** — the app's client id and name when a connected app acted
