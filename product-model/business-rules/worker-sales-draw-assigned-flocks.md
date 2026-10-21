---
appliesTo:
- type: capability
  id: confirm-sales-order
- type: entity
  id: egg-lot
  facts:
  - Available
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/ConfirmSale/ConfirmSaleHandler.cs#ConfirmSaleHandler
- kind: doc
  role: context
  target: docs/designs/612-worker-sales-allocation-policy.md
---

# A restricted Worker's sale draws only on their assigned flocks

Under Assigned flocks only, a Worker with flock assignments confirms orders from their own flocks' lots. When those fall short but the farm's stock would cover the order, the refusal is generic and names no flock or quantity.
