---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/RecordPayment/RecordPaymentHandler.cs
- kind: code
  role: implementation
  target: web/src/routes/sales/usePayments.tsx
---

# Record a payment

Record money received against a confirmed order: date, amount, method and an optional reference and note. Partial payments are normal; paying more than is outstanding is refused.
