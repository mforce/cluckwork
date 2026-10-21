---
entities:
- entity: sales-order
  shows:
  - Reference
  - Order date
  - Total
  - Discount
  - Outstanding
  collects:
  - Order date
- entity: customer
  shows:
  - Name
entryPoints:
- farm-web: /sales
references:
- kind: code
  role: implementation
  target: web/src/routes/sales/SalesPage.tsx
- kind: code
  role: implementation
  target: web/src/routes/sales/OrderList.tsx
---

# Sales

The list of sales orders, filtered by status and customer or to unpaid orders only, where a new order is started.
