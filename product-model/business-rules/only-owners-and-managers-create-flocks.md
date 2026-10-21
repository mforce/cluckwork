---
appliesTo:
- type: entity
  id: flock
  effect: creates
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
- kind: doc
  role: context
  target: docs/designs/388-worker-flock-read-scoping.md
---

# Only Owners and Managers create a flock

No Worker creates a flock, restricted or not: there is nothing yet to scope a new flock to, so flock creation is administration.
