---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: stock
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/EggOperations/Stock/StockEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggLotRepository.cs
- kind: code
  role: implementation
  target: web/src/routes/StockPage.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/StockTools.cs
---

# View stock

See egg stock per grade: available eggs, restricted eggs and the grade's low-stock floor, with a warning for each grade below its floor.
