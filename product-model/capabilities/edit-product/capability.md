---
availability:
- place: farm-web::setup
domain: products
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Catalog/UpdateProduct/UpdateProductHandler.cs
---

# Edit a product

Change a product's name, grade, selling unit, default price or notes. Its type is fixed, and changes affect only lines added afterwards.
