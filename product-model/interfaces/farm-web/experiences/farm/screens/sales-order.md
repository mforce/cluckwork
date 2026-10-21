---
entities:
- entity: sales-order
  shows:
  - Reference
  - Order date
  - Lines
  - Total
  - Discount
  - Discount reason
  - Discount reason note
  - Outstanding
  - Void reason
  collects:
  - Lines
  - Discount reason
  - Discount reason note
  - Void reason
- entity: customer
  shows:
  - Name
- entity: product
  shows:
  - Name
  - Selling unit
  - Default price
- entity: payment
  shows:
  - Date
  - Amount
  - Method
  - Reference
  - Note
  - Void reason
  collects:
  - Date
  - Amount
  - Method
  - Reference
  - Note
  - Void reason
references:
- kind: code
  role: implementation
  target: web/src/routes/sales/OrderLines.tsx
- kind: code
  role: implementation
  target: web/src/routes/sales/AddLineForm.tsx
- kind: code
  role: implementation
  target: web/src/routes/sales/SettlementRail.tsx
---

# Sales order

One open order: its lines with list price and discount, its total, the confirm, cancel and void actions, and the payments that settle it.
