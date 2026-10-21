---
appliesTo:
- type: entity
  id: egg-lot
  effect: changes
  contexts:
  - place: farm-web::farm::history
  - place: farm-web::farm::sales-order
permits:
- actors:
  - owner
  - manager
  - sales
  - worker
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
---

# Owners, Managers, Sales and Workers move egg lots only through days and sales they may change

Outside Stock corrections, a lot changes only as the effect of an operation governed elsewhere: adjusting or voiding a day, and confirming or voiding a sale.
