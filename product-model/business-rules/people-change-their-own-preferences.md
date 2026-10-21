---
appliesTo:
- type: entity
  id: user
  effect: changes
  facts:
  - UI language
  - Counting unit
permits:
- related:
  - verb: signs-in-as
    entity: owner
- related:
  - verb: signs-in-as
    entity: manager
- related:
  - verb: signs-in-as
    entity: worker
- related:
  - verb: signs-in-as
    entity: sales
- related:
  - verb: signs-in-as
    entity: read-only
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Me/MeEndpoints.cs
---

# Only a person changes their own language and counting unit

Preferences belong to the person.
