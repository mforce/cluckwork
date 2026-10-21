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
---

# A restricted Worker's sale draws on every flock on the farm

Under All farm flocks, a Worker with flock assignments confirms orders from any lot, oldest first, like every other seller; an insufficient-stock refusal still carries no grade or quantity detail.
