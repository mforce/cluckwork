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
  target: web/src/routes/useStockLots.ts
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/StockTools.cs
---

# Browse egg lots

Trace stock to its source: a grade's lots by production date with the flock that laid them, what each produced and has left, any restriction and the daily entry that created it, and a lot's movement ledger of production, sales, voids and corrections.
