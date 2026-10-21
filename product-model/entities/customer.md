---
relations:
- entity: sales-order
  verb: places
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/Customer.cs#Customer
---

# Customer

Someone the farm sells to.

## Information kept

- **Name** — required
- **Phone** — required
- **Email** — optional
- **Address** — optional
- **Note** — optional
- **Outstanding balance** — confirmed order totals less non-voided payments, summed across the customer's orders
