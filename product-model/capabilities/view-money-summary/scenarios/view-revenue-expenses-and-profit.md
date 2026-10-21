---
kind: primary
routes:
  web: Web
steps:
- text: The Manager opens the Money tab of Reports for a date range
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::reports
- text: The Product totals confirmed orders' revenue, paid and outstanding amounts and the eggs on their lines, totals recorded expenses, divides the range's feed cost by the dozens of eggs collected, and subtracts expenses from revenue
  kind: product
  actor: manager
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Total
    - Outstanding
  - entity: expense
    effect: reads
    facts:
    - Amount
  - entity: feed-usage
    effect: reads
    facts:
    - Date
    - Estimated cost
  - entity: daily-entry
    effect: reads
    facts:
    - Date
    - Total eggs
  contexts:
    web:
      place: farm-web::farm::reports
- text: The Product reads the same figures for the previous period of the same length and shows each headline figure's change beside it
  kind: product
  actor: manager
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Total
  - entity: expense
    effect: reads
    facts:
    - Amount
  - entity: feed-usage
    effect: reads
    facts:
    - Date
    - Estimated cost
  - entity: daily-entry
    effect: reads
    facts:
    - Date
    - Total eggs
  contexts:
    web:
      place: farm-web::farm::reports
---

# View revenue, expenses and profit

## Trigger

An Owner or Manager checks the farm's money.

## Outcome

They see revenue, eggs sold, expenses, feed cost per dozen with the range's total feed cost, and basic profit with both operands shown, each beside its change against the previous period.

## Edge cases

- Eggs sold count only egg lines of the range's confirmed orders, the same orders revenue sums; draft and voided orders never count.
- Feed cost per dozen counts only official days' eggs. A range with no eggs has no figure, and feed taken from a lot with no unit cost adds nothing, so the figure can read low.
- A previous value of zero or none shows no change, and a failed read of the previous period leaves the current figures without changes.
