---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/CreateSalesOrder/CreateSalesOrderHandler.cs#CreateSalesOrderHandler
- kind: code
  role: implementation
  target: web/src/routes/sales/useNewOrder.tsx
---

# Create a sales order

Start a Draft order for a customer on a day no later than the farm's today. The order takes a generated reference and the farm's currency, and opens so lines can be added.
