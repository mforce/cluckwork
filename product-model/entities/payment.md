---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/Payment.cs
---

# Payment

Money received against a confirmed sales order.

## Information kept

- **Date** — the farm day received, never in the future
- **Amount** — in the order's currency; never more than the order's outstanding amount
- **Method** — Cash, Check, Card, Bank transfer, Mobile payment or Other
- **Reference** — an optional payment reference
- **Note** — an optional note
- **Void reason** — why the payment was voided

## States

### Recorded

Counts against the order's outstanding amount.

### Voided

A wrong payment undone with a reason; the outstanding grows back.
