---
appliesTo:
- type: entity
  id: egg-grade
  effect: changes
  facts:
  - Low-stock floor
permits:
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggGrades/EggGradeFloorPolicy.cs
- kind: doc
  role: context
  target: docs/decisions/729-owner-only-farm-configuration.md
---

# Only an Owner sets or clears a grade's low-stock floor

The floor is farm configuration; Managers and everyone below read it.
