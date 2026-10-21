---
relations:
- entity: flock
  verb: keeps
  cardinality: one-to-many
- entity: user
  verb: employs
  cardinality: one-to-many
- entity: customer
  verb: keeps
  cardinality: one-to-many
- entity: egg-grade
  verb: keeps
  cardinality: one-to-many
- entity: packed-unit
  verb: keeps
  cardinality: one-to-many
- entity: inventory-item
  verb: keeps
  cardinality: one-to-many
- entity: expense-category
  verb: keeps
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Farm/Accounts/Account.cs#Account
- kind: doc
  role: context
  target: specs/product/GLOSSARY.md
---

# Farm

One farm on the server: the tenant every record belongs to, with the settings that decide how it reads and behaves. People sign in to a farm by its farm code.

## Information kept

- **Name** — the farm's own name shown in the app
- **Farm code** — the short, stable, URL-safe code people type at sign-in; lowercase letters, digits and hyphens, 3 to 32 characters, unique on the server
- **Timezone** — the IANA timezone that decides the farm's operational day; set when the farm is provisioned, UTC if none is given
- **Locale** — a region locale such as en-US, stored for number and date formatting
- **Currency** — the currency new amounts are recorded in, with its symbol and minor unit
- **Unit system** — Metric or Imperial
- **First day of week** — an optional override of the week's first day
- **Date and time formats** — optional date and time format overrides
- **Palette** — the farm-wide accent palette: Aubergine, Forest, Slate or Terracotta
- **Default counting unit** — the packed unit one tap of a Daily entry stepper counts by when a person has not chosen their own
- **Worker sale allocation** — Assigned flocks only or All farm flocks: which flocks a restricted Worker's sale may draw stock from
- **Discount ceiling** — the largest percentage below list price a Sales user or Worker may sell a line at; blank means no ceiling and zero means no discount
- **Connected apps** — On or Off: whether connected apps may act for people on the farm
- **Logo** — an optional still PNG, JPEG or WebP image shown in the app's chrome
- **Banner** — an optional still PNG, JPEG or WebP image shown on the post-login splash

## States

### Active

People can sign in and work.

### Suspended

The farm is offline: its people are refused at sign-in and on their next request, and nothing is deleted.
