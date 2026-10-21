---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/SaleEndpoints.cs
- kind: code
  role: implementation
  target: web/src/routes/sales/OrderList.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/CommerceTools.cs
---

# Browse sales orders

List the farm's orders filtered by status and customer, or to unpaid orders only, with each order's total, outstanding amount and discount. Only Owners, Managers and Sales see what an order still owes.
