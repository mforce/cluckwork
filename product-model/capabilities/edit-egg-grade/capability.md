---
availability:
- place: farm-web::setup
domain: grades
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggGrades/UpdateEggGrade/UpdateEggGradeHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggGrades/EggGradeFloorPolicy.cs
---

# Edit an egg grade

Change a grade's name, sort order, saleability or low-stock floor. Its type is fixed after creation, and only an Owner sets or clears the floor.
