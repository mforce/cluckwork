---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/CancelSalesOrder/CancelSalesOrderHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrder.cs#Cancel
---

# Cancel a sales order

Abandon a Draft order. Closing the order only hides it; cancelling ends it.
