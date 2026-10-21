---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/UpdateOrderItem/UpdateOrderItemHandler.cs
- kind: code
  role: implementation
  target: web/src/routes/sales/OrderLines.tsx
---

# Edit an order line

Change a Draft line's quantity or unit price.
