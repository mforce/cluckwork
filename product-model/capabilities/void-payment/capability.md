---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/VoidPayment/VoidPaymentHandler.cs
---

# Void a payment

Undo a wrong payment with a reason; the outstanding amount grows back.
