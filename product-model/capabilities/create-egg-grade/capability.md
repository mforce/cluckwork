---
availability:
- place: farm-web::setup
domain: grades
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggGrades/CreateEggGrade/CreateEggGradeHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggGrades/CreateEggGrade/CreateEggGradeValidator.cs
---

# Create an egg grade

Add a grading bucket with its name, type, sort order and whether it is saleable.
