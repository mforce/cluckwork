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
---

# Read-only

A person who reads stock, history and reports and changes nothing.
