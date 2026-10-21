---
availability:
- place: farm-web::farm
domain: customers
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Customers/CreateCustomer/CreateCustomerHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Customers/CreateCustomer/CreateCustomerValidator.cs
---

# Create a customer

Add someone the farm sells to, with a name and phone number and optional email, address and note.
