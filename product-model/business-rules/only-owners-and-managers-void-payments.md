---
appliesTo:
- type: entity
  id: payment
  effect: changes
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/PaymentEndpoints.cs
---

# Only Owners and Managers void a payment

Undoing a payment is corrective, like every other undo.
