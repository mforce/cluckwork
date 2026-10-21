---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: customers
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Customers/CustomerEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Sales/PaymentEndpoints.cs
- kind: code
  role: implementation
  target: web/src/routes/CustomersPage.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/CommerceTools.cs
---

# Browse customers

List the farm's customers by name. Owners and Managers also see what each customer owes on the Customers screen; through a connected app, Owners, Managers and Sales read balances, and contact details are never returned.
