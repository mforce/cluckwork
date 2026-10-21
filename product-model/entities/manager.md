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

# Manager

A person who runs farm operations: corrections, operational catalogs, flocks, stock write-offs, money reports and expenses, but not farm settings or users.
