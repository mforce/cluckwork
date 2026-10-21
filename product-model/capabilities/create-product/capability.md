---
availability:
- place: farm-web::setup
domain: products
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Catalog/CreateProduct/CreateProductHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Catalog/CreateProduct/CreateProductValidator.cs
---

# Create a product

Add an egg product with its name, the egg grade it draws from, its selling unit and an optional default price, in the farm's currency.
