---
kind: primary
routes:
  web: Web
steps:
- text: The Read-only person opens the Production tab of Reports and chooses a date range
  kind: actor
  actor: read-only
  entities: []
  contexts:
    web:
      place: farm-web::farm::reports
- text: The Product totals official daily entries per day, per flock that filed in the range and for the period, and rates hen-day percentage only over the flocks that recorded each day
  kind: product
  actor: read-only
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Date
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Sellable
    - Mortality
    - Grading
    - Condition grades
  - entity: flock
    effect: reads
    facts:
    - Name
    - Placement date
    - Current birds
  contexts:
    web:
      place: farm-web::farm::reports
- text: The Product reads the same totals for the previous period of the same length and shows each headline figure's change beside it
  kind: product
  actor: read-only
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Date
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Sellable
    - Condition grades
  contexts:
    web:
      place: farm-web::farm::reports
---

# View daily production

## Trigger

Someone reviews how the flocks are laying.

## Outcome

They see each day's production, each flock's totals and hen-day percentage, and a period hen-day percentage of period eggs over period recorded hen-days. The period's sellable and egg loss percentages add up to 100, and eggs, sellable, losses and the three rates each show how they moved against the previous period.

## Edge cases

- A day nobody recorded has no percentage rather than zero, and a flock with no recorded hen-days in the range has none either.
- A figure above 100% is shown as computed and flagged for checking the bird counts.
- Cracked and Dirty eggs count as sellable only where the day resolved them to a grade when it became official, so changing those grades later never restates a past period.
- A period with no eggs has no sellable or egg loss percentage.
- Only flocks that filed an official entry in the range get a row in the flock breakdown; with more than ten, the ten with the most eggs show first and Show all reveals the rest.
- Rates change in percentage points and counts in percent. A previous value of zero or none shows no change, and a failed read of the previous period leaves the current figures without changes.
- Too many report requests at once from one farm are refused with a retry-after rather than queued.
