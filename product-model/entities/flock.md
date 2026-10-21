---
relations:
- entity: daily-entry
  verb: has
  cardinality: one-to-many
- entity: bird-movement
  verb: records
  cardinality: one-to-many
- entity: flock-assignment
  verb: scopes
  cardinality: one-to-many
- entity: feed-usage
  verb: consumes
  cardinality: one-to-many
- entity: water-usage
  verb: consumes
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/FlockManagement/Flocks/Flock.cs#Flock
---

# Flock

A group of birds managed as one unit, defined by breed and placement date. Production, mortality, feed and water all hang off a flock.

## Information kept

- **Name** — the flock's name, used to pick it everywhere
- **Breed** — the birds' breed
- **Placement date** — when the birds were placed in the house; age counts from here
- **Initial count** — birds placed on day one; never changed by deaths
- **Current birds** — initial count minus every bird movement, derived from the bird ledger
- **Depleted on** — the farm day the flock was depleted
- **Archived on** — the farm day the flock was archived

## States

### Active

Laying: accepts daily entries, movements, feed and water for any date up to the farm's today.

### Depleted

The birds are gone: still listed, and accepts backfill dated on or before the depletion date only.

### Archived

Hidden bookkeeping: gone from pickers and the Dashboard, visible on Flocks behind a toggle, and accepts nothing new.
