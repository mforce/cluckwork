---
appliesTo:
- type: capability
  id: change-farm-settings
- type: entity
  id: farm
  facts:
  - Currency
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Farm/Accounts/CurrencyBoundRowProbe.cs
---

# The farm currency cannot change once any amount is recorded in it

No sales order, payment, expense, priced product or feed money may exist; otherwise the whole settings save is refused. A farm that truly changes operating currency gets a new farm.

## Rationale

Every recorded amount keeps its currency forever, and a catalogue price taken onto an order would be relabelled in the new currency.
