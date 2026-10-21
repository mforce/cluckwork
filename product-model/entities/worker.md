---
kind: person
acts: external
relations:
- entity: user
  verb: signs-in-as
  cardinality: one-to-one
- entity: app-connection
  verb: approves
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Users/AssignFlock/AssignFlockHandler.cs
- kind: doc
  role: context
  target: docs/designs/388-worker-flock-read-scoping.md
---

# Worker

A person with no elevated role who records the day's work: daily entries, feed, water and purchases, and who may build and confirm sales orders. Flock assignments can restrict a Worker to some flocks.

## Information kept

- **Flock scope** — All flocks while the Worker has no flock assignment or a farm-wide one; Assigned flocks once the Worker holds an assignment naming a flock
