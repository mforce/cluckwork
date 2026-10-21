---
actors:
- sales
- worker
references:
- kind: code
  role: implementation
  target: web/src/routes/sales/useNewOrder.tsx
---

# Sell eggs to a customer

## Goal

A seller turns a customer's request into a confirmed order with its eggs allocated.

## Success criterion

The order is Confirmed and its eggs have left available stock from identifiable lots.
