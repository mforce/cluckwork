---
appliesTo:
- type: entity
  id: sales-order
  effect: reads
  facts:
  - Outstanding
permits:
- actors:
  - owner
  - manager
  - sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/SaleEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/PaymentEndpoints.cs
---

# Only Owners, Managers and Sales read what an order owes

An order's outstanding amount, and the Unpaid only filter, are the money tier's. A Worker's order list carries no outstanding figure, and a Worker asking for unpaid orders is refused. Unpaid means a confirmed order with more than zero outstanding.
