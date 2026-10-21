---
kind: validation
routes:
  web: Web
steps:
- text: The Worker trims the total eggs below what is already graded and chooses Save draft
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product refuses to save because the grading exceeds total eggs minus cracked, dirty and discarded
  kind: condition
  actor: worker
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Sellable
    - Grading
  contexts:
    web:
      place: farm-web::farm::daily-entry
---

# Refuse grading beyond the sellable count

## Trigger

A draft's grading adds up to more than the day's sellable eggs.

## Outcome

Nothing is saved; the Left to grade figure reads over until the numbers agree.

## Edge cases

- Cracked, dirty and discarded together exceeding the total is refused the same way.
- A Cracked or Dirty grade named as a hand-graded line is refused: those eggs are captured by their own counters.
