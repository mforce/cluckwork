---
entities:
- entity: customer
  shows:
  - Name
  - Phone
  - Email
  - Address
  - Note
  - Outstanding balance
  collects:
  - Name
  - Phone
  - Email
  - Address
  - Note
entryPoints:
- farm-web: /customers
references:
- kind: code
  role: implementation
  target: web/src/routes/CustomersPage.tsx
---

# Customers

The farm's customers, each linking to their orders on Sales; Owners and Managers also see what each customer owes.
