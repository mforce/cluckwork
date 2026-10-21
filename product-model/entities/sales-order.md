---
relations:
- entity: payment
  verb: settled-by
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrder.cs#SalesOrder
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrderItem.cs
---

# Sales order

An order of egg products for one customer. Confirming it allocates stock from egg lots oldest first; voiding it returns those eggs to the exact lots they came from.

## Information kept

- **Reference** — a generated order reference such as SO-1A2B3C4D
- **Order date** — the farm day of the order, never in the future
- **Currency** — the farm currency when the order was created, kept for good
- **Lines** — per line: product, the grade it draws from, packed unit and eggs per unit as of the line, quantity in units, eggs, unit price, list price and list price basis
- **Total** — the sum of line totals, recalculated on every change
- **Discount** — per line and per order: how far comparable lines sold below list price, derived for display
- **Discount reason** — Volume, Damaged stock, Long-standing customer, Manager approved or Other, recorded at confirm
- **Discount reason note** — free text beside the reason, required for Other
- **Allocations** — which egg lots each line drew from at confirm, and how many eggs
- **Outstanding** — the confirmed total less non-voided payments
- **Void reason** — why a confirmed order was voided

## States

### Draft

Fully editable: lines are added, changed and removed.

### Confirmed

Stock allocated; payments can be recorded.

### Cancelled

A draft abandoned before confirming.

### Voided

A mistaken confirm undone with a reason; the order stays listed with its lines and total.
