---
appliesTo:
- type: entity
  id: payment
  effect: creates
permits:
- actors:
  - owner
  - manager
  - sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/PaymentEndpoints.cs
---

# Only Owners, Managers and Sales record a payment

Recording money is the Sales tier.
