---
appliesTo:
- type: capability
  id: record-payment
- type: entity
  id: payment
  facts:
  - Amount
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/RecordPayment/RecordPaymentHandler.cs
---

# A payment never exceeds the order's outstanding amount

Payments attach only to confirmed orders, take the order's currency, and are checked under the order's lock so racing payments cannot overshoot.
