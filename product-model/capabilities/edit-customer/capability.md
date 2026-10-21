---
availability:
- place: farm-web::farm
domain: customers
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Customers/UpdateCustomer/UpdateCustomerHandler.cs
---

# Edit a customer

Change a customer's details in place. Customers are never deleted, archived or merged.
