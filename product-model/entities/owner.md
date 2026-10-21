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

# Owner

A person with the Admin role, shown as Admin in role pickers and called the Owner in the app's messages. Owners do everything, including farm settings, branding, users and connected apps.
