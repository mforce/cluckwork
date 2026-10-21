---
availability:
- place: farm-web::setup
domain: grades
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggGrades/SetEggGradeActive/SetEggGradeActiveHandler.cs
---

# Deactivate an egg grade

Remove a grade from capture and order pickers. Existing stock stays counted and lines added before deactivation can still confirm. Grades are never deleted.
