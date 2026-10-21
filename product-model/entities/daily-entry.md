---
relations:
- entity: egg-lot
  verb: produces
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/DailyEntry.cs#DailyEntry
- kind: doc
  role: context
  target: docs/architecture.md
---

# Daily entry

One flock's production record for one operational day: total eggs, losses, mortality and the graded breakdown of sellable eggs. Submitting it creates the day's egg lots.

## Information kept

- **Date** — the farm day recorded, never in the future
- **Total eggs** — all eggs collected
- **Cracked** — cracked eggs set aside
- **Dirty** — dirty eggs set aside
- **Discarded** — eggs discarded, always a loss
- **Mortality** — birds that died that day
- **Grading** — the sellable eggs counted per egg grade
- **Sellable** — total minus cracked, dirty and discarded: the figure grading counts down to
- **Condition grades** — which grade the Cracked and Dirty counts became stock in when the entry became official, or none when that condition was a loss
- **Adjust reason** — why the entry was last adjusted
- **Values before adjustment** — the totals and grading the last adjustment replaced
- **Void reason** — why the entry was voided
- **Locked at** — when the entry locked

## States

### Draft

Saved work in progress: editable, possibly graded only partly, and nothing downstream exists yet.

### Submitted

Official: frozen for Workers, with its egg lots and mortality created.

### Locked

Submitted more than seven farm days ago; Owners and Managers can still adjust or void it.

### Adjusted

An Owner or Manager corrected its totals or grading after it became official.

### Voided

Undone with a reason: its lots emptied, its mortality reversed, and its day free to record again.
