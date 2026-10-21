---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Catalog/Product.cs#Product
---

# Product

What the farm sells. Each egg product maps to one egg grade, whose lots sales draw from.

## Information kept

- **Name** — unique within the farm, ignoring case
- **Product type** — Egg; fixed after creation
- **Egg grade** — the grade sales of this product draw from
- **Selling unit** — the default packed unit
- **Default price** — an optional price per selling unit
- **Currency** — the farm currency when the product was created
- **Notes** — optional notes

## States

### Active

Offered on new order lines.

### Inactive

Hidden from pickers while history keeps it.
