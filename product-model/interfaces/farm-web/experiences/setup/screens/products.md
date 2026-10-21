---
entities:
- entity: product
  shows:
  - Name
  - Product type
  - Egg grade
  - Selling unit
  - Default price
  - Currency
  - Notes
  collects:
  - Name
  - Product type
  - Egg grade
  - Selling unit
  - Default price
  - Notes
- entity: packed-unit
  shows:
  - Unit
  - Eggs per unit
  - Active
  collects:
  - Eggs per unit
  - Active
entryPoints:
- farm-web: /products
references:
- kind: code
  role: implementation
  target: web/src/routes/ProductsPage.tsx
- kind: code
  role: implementation
  target: web/src/routes/PackedUnitsPanel.tsx
---

# Products

What the farm sells, each mapped to an egg grade, and the farm's packed units.
