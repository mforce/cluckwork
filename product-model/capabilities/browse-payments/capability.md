---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/PaymentEndpoints.cs
- kind: code
  role: implementation
  target: web/src/routes/sales/SettlementRail.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/CommerceTools.cs
---

# Browse an order's payments

See the payments recorded against one confirmed order, live and voided, with what is still outstanding.
