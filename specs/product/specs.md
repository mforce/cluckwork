# Cluckwork — Reconciled Product & Technical Specification v4

**Product:** Cluckwork
**Domain:** Poultry egg-producing farm management, with future support for chicken raising, pullets, broilers, live bird sales, meat products, breeders, and hatchery modules
**Version:** v4.4 spec-corrections patch (egg unit conversion, grading assumption, hen-day fix)
**Purpose:** Provide one coherent developer-ready specification. This replaces the v3 addendum + embedded v2 structure with a single integrated document.

---

## 0.1 Naming Update

The project has been renamed from **Egg Farm Manager** to **Cluckwork**. The underlying product scope remains the same: egg-producing poultry farm management first, with future extensibility for pullets, broilers, live bird sales, meat products, breeders, and hatchery modules.

## 0. v4 Reconciliation Summary

This version fixes the editorial inconsistencies identified in the Opus 4.8 review:

1. There is now **one canonical sales model**: product-generic sales order items.
2. Egg sales still use **egg lots and egg-lot allocations** for Phase 1 traceability.
3. The old egg-only `sales_order_items` schema is removed.
4. The old generic `sales_order_item_allocations` name is replaced with the canonical `sales_order_item_egg_allocations`.
5. There is now **one phase plan** and **one sprint list**.
6. Wireframe coverage is documented as:
   - current v4 wireframes,
   - v2 wireframes carried forward unchanged,
   - v2 wireframes superseded/redrawn in v4.
7. Product-purpose/model validity is defined.
8. Daily-entry section visibility is defined for `egg`, `meat`, `raising`, `breeding`, and `mixed`.

---

# 1. Product Goal

Build a farm management system that can run an egg-producing poultry farm today and later expand into broader poultry operations.

The Phase 1 product should answer:

- How many eggs were produced today?
- Which flock/house produced them?
- Which egg lots are available by grade, date, flock, location, and restriction status?
- Are any eggs restricted due to medication withdrawal?
- How much feed and water were consumed?
- What is the flock’s hen-day production?
- What is the current live bird count?
- What was sold, to whom, and from which egg lots?
- What is the farm’s sales, expense, and profitability picture?

The architecture should later support:

- Pullet/chicken raising
- Broilers/meat birds
- Live bird sales
- Processed meat products
- Breeders
- Hatchery
- Manure/byproduct sales
- Multi-species poultry operations

---

# 2. Non-Goals for Phase 1

Phase 1 does **not** include:

- Broiler growout optimization
- Meat processing inventory
- Hatchery management
- Breeder flock fertility/hatchability
- Feed formulation
- Payroll
- Full accounting replacement
- Offline mobile mode
- IoT/sensor automation
- Processing plant management
- Delivery route optimization

The schema reserves paths for these modules, but they are not built in Phase 1.

---

# 3. Core Architecture

## 3.1 Tenant/account hierarchy

The system includes an account root from day one.

```text
Account / Tenant
  Users
  Farms
    Houses
      Flocks
```

```text
accounts
- id
- name
- owner_user_id
- plan_type
- status
- created_at
- updated_at
```

Every business table should include `account_id`.

## 3.2 Farm hierarchy

```text
farms
- id
- account_id
- name
- address
- phone
- email
- timezone: IANA timezone string, e.g. America/Los_Angeles
- locale: BCP 47 locale tag, e.g. en-US, es-MX, ja-JP
- currency_code: ISO 4217 code, e.g. USD, MXN, JPY
- currency_symbol nullable
- currency_minor_unit: 0 / 2 / 3, derived from currency where possible
- unit_system: imperial / metric
- first_day_of_week nullable
- date_format_override nullable
- time_format_override nullable
- active
- created_at
- updated_at
```

A farm may also have a **logo**, used as branding in the app chrome. It is not a
column on the row above: the farm record is read on every dated and every priced
operation, and an image would then be fetched along with it every time. The image
lives in its own one-row-per-farm table (#123).

```text
farm_logos
- id
- account_id
- farm_id: unique — one logo per farm
- content: the image bytes, PNG / JPEG / WebP only, 1 MB max, still images only
- content_type: sniffed from the bytes, never the uploaded declaration
- width / height: read from the image header, capped
- byte_length: stored, not derived — see below
- version: optimistic concurrency token — orders two writers replacing the same logo
- content_hash: identifies the current logo; serves as the HTTP ETag
- updated_at
```

`byte_length` is a column rather than a `length(content)` call because `content`
is TOAST-compressed: measuring it would fetch and decompress the megabyte, which
is exactly what the metadata-only reads exist to avoid.

Uploads are Owner/Manager only. SVG is refused outright — it is a document that
can carry script, and the app renders this image back to every user of the farm.
What is stored is never the uploaded file: the container is walked and rewritten,
which drops metadata blocks (EXIF on a phone photo carries GPS coordinates —
for a farm, its physical location) and discards anything appended past the
image's own end marker. Animation is refused in both formats: an animated WebP
frame nests its own chunk stream, which a flat sweep cannot reach, and holding
PNG to the same rule keeps it one sentence rather than a per-format footnote.

```text
houses
- id
- account_id
- farm_id
- name
- house_type: cage / deep_litter / free_range / aviary / other
- capacity
- location_description
- active
- notes
- created_at
- updated_at
```

## 3.3 Flock model

Flocks are not hardcoded to layers. They classify what kind of poultry operation they represent.

```text
flocks
- id
- account_id
- farm_id
- house_id
- name
- species: chicken / duck / quail / turkey / other
- production_purpose: layer / broiler / pullet / breeder / dual_purpose / other
- production_model: egg / meat / raising / breeding / mixed
- sex: female / male / mixed / unknown
- breed_or_strain
- source_supplier_id nullable
- placement_date
- starting_bird_count
- production_stage: brooding / growing / pre_lay / laying / molting / growout / finishing / breeding / depleted
- status: active / inactive / depleted / sold
- expected_start_lay_date nullable
- expected_harvest_date nullable
- expected_depletion_date nullable
- active_egg_withdrawal_until nullable
- beak_treated_status: unknown / no / yes / supplier_performed
- notes
- created_at
- updated_at
```

## 3.4 Valid flock purpose/model combinations

The system should validate combinations at the application layer. These are the recommended defaults:

| production_purpose | Allowed production_model values | Notes |
|---|---|---|
| layer | egg, mixed | Normal egg-producing flock |
| broiler | meat | Meat bird flock |
| pullet | raising | Chick/pullet raising until transfer or sale |
| breeder | breeding, mixed | Future breeder module |
| dual_purpose | mixed, egg, meat | May produce eggs and later meat/live bird sales |
| other | egg, meat, raising, breeding, mixed | Admin-configurable exception |

Invalid by default:

| Combination | Reason |
|---|---|
| broiler + egg | Broilers are not managed as egg-producing flocks |
| layer + meat | Use `dual_purpose + mixed` if the flock may also be sold/depleted as birds |
| pullet + egg | Use layer once transferred into laying production |
| pullet + meat | Use broiler if the purpose is meat production |

Admin override may be allowed later, but defaults should guide users toward clean reporting.

---

# 4. Core Design Principles

## 4.1 Ledger-first inventory

Never directly edit stock balance.

Every stock change must create a movement.

Cached balances are allowed only if they can be rebuilt from movement ledgers.

This applies to:

- Egg lots
- Feed
- Supplements
- Medications
- Vaccines
- Packaging
- Bedding/litter
- Future meat lots
- Future live bird allocations where relevant

## 4.2 Traceability-first eggs

Egg inventory is lot-based.

```text
Egg Lot = farm + house + flock + production_date + egg_grade + location + quantity
```

Dashboards may show aggregate inventory by grade, but the source of truth is lots.

## 4.3 Product-generic sales

Sales are generic.

A sales order item references a `product_id`.

Product-specific allocation tables handle inventory source:

- Egg product → egg lots
- Future live bird product → bird ledger/flock allocation
- Future meat product → meat lots
- Other products/services → no stock allocation or a future module-specific allocator

## 4.4 Operational dates

Farm operations use farm-local dates.

Audit timestamps use UTC.

```text
daily_entry.date = farm-local date
created_at / updated_at = UTC
```


## 4.5 Farm localization settings

For Phase 1, formatting and operational localization (numbers, dates, money, timezone, units) belongs to the farm. UI language is the one exception: it is a per-user preference — see *UI language vs farm locale* below.

```text
Farm = currency + locale + timezone
```

The account may own multiple farms later, but each farm has its own display and reporting defaults.

### Canonical fields

| Field | Purpose |
|---|---|
| `farms.timezone` | Determines farm-local operational dates and default "today" |
| `farms.locale` | Determines number, date, time, and currency formatting |
| `farms.currency_code` | Determines the currency used for prices, sales, payments, expenses, and reports |
| `farms.currency_minor_unit` | Determines how integer money amounts are interpreted |
| `farms.first_day_of_week` | Used for weekly reports and calendars |
| `farms.unit_system` | Determines default display units for feed, water, and weights |

### Phase 1 currency rule

Phase 1 is single-currency per farm.

```text
All sales, payments, expenses, product prices, and financial reports for a farm use farms.currency_code.
```

There is no exchange-rate conversion in Phase 1.

If an account has multiple farms with different currencies, the system may show farm-specific reports, but should not automatically aggregate financial totals across currencies.

### Money storage rule

Money is stored as integer minor units using the farm currency.

Examples:

```text
USD 12.34 → amount_cents = 1234, currency_minor_unit = 2
JPY 1200 → amount_cents = 1200, currency_minor_unit = 0
```

The field name `amount_cents` remains acceptable in code for Phase 1, but the more precise long-term name is `amount_minor_units`.

### Display rule

The UI should format money, dates, times, and numbers using:

```text
farm.locale + farm.currency_code + farm.timezone
```

UI string language is selected separately, per `users.language` — see *UI language vs farm locale* below. Language never changes formatting.

Examples:

```text
en-US + USD + America/Los_Angeles → $1,234.56, 06/26/2026
es-MX + MXN + America/Mexico_City → $1,234.56 MXN, 26/06/2026
ja-JP + JPY + Asia/Tokyo → ￥1,235, 2026/06/26
```

### Operational date rule

Daily entry, mortality, feed usage, water usage, egg production, and sales order dates default to the selected farm’s local date.

Audit timestamps remain UTC.

### UI language vs farm locale

UI language (translated interface strings) is a separate concern from farm locale (formatting).

| Concern | Field | Scope |
|---|---|---|
| Formatting conventions for numbers/dates/money (together with `farms.timezone`, the currency fields, and the format overrides — see the display rule above) | `farms.locale` | Per farm |
| UI language (translated strings) | `users.language` | Per user |

Field format:

- `users.language` stores a BCP 47 **primary-language subtag** (`en`, `es`, `ja`), not a full locale. Language packs are keyed by that subtag; regional variants are a formatting concern and stay with `farms.locale`.

Catalog resolution (deterministic, in order):

1. `users.language`, if set **and** a matching language pack exists.
2. The language component of the user's **default farm** locale (`default_farm_id`; e.g. `es-MX` → `es`), if a matching pack exists. Switching the active farm mid-session does not change UI language.
3. English.

A stored `users.language` with no matching pack (stale/unsupported value) is treated as unset — resolution continues down the chain; it is not an error.

Within the resolved catalog, a missing translation key falls back to the English string, never a blank or raw key.

Bootstrap and self-service:

- The login response (and any current-user/profile endpoint) includes `language`, so the SPA resolves the catalog before first paint. The JWT does not carry language; a change takes effect on the next catalog load, not via token refresh.
- Every authenticated user — including Read-only (§5.1) — may update their **own** `users.language`. This is a self-service profile preference, not a farm-scoped permission.

API error localization:

- Validation failures (400) carry a stable machine-readable per-field code; domain rule violations (422) already carry a `code` (technical spec §3.2). Clients localize from the code; the human-readable message text is an English fallback, not a contract. Other response classes (auth, idempotency, concurrency) are unchanged.

Formatting is unaffected by UI language: a user reading the UI in English still sees the farm's `es-MX` number/date/money formats.

Phasing: i18n infrastructure (string externalization, message codes, `users.language`) lands in Phase 1.1 and ships English-only; the first non-English translation (Spanish) lands in Phase 1.5 (§6).

## 4.6 Financial row currency immutability

Financial rows must be self-contained for historical interpretation.

### Row-level currency rule

All money display and all financial reports must interpret `amount_cents` using the **row's stored** currency fields, never the farm's current currency settings.

Use:

```text
row.currency_code
row.currency_minor_unit
```

Do not use:

```text
farm.currency_code at display/report time
```

except when creating a new financial row.

### Required financial row snapshots

The following rows must snapshot both fields at creation:

```text
sales_orders.currency_code
sales_orders.currency_minor_unit

payments.currency_code
payments.currency_minor_unit

expenses.currency_code
expenses.currency_minor_unit
```

Optional future financial tables should follow the same rule.

### Farm currency change rule

For Phase 1, block changing `farms.currency_code` if any financial row already exists for the farm:

```text
sales_orders
payments
expenses
```

If no financial rows exist, the farm currency may be changed.

If a farm truly changes operating currency after transactions exist, recommended workflow is:

```text
Create a new farm record or future currency-migration workflow.
Do not reinterpret old financial records.
```

### Currency derivation fallback

Phase 1 may ship with a static ISO 4217 lookup table.

If `currency_symbol` cannot be derived:

```text
Use currency_code as the display symbol.
```

If `currency_minor_unit` cannot be derived:

```text
Default to 2.
```

Examples:

```text
USD → symbol "$", minor unit 2
JPY → symbol "¥", minor unit 0
Unknown XYZ → symbol "XYZ", minor unit 2
```


---

# 5. Users, Roles, and Scope

## 5.1 Roles

| Role | Purpose |
|---|---|
| Owner | Full access |
| Manager | Farm operations, inventory, health, reports |
| Worker | Daily entry for assigned houses/flocks |
| Sales | Customers, sales, payments |
| Vet/Consultant | Health, medication, vaccination, welfare notes |
| Read-only | View dashboards/reports only |

## 5.2 Role assignments

```text
users
- id
- account_id
- name
- email
- password_hash
- default_farm_id
- language nullable: BCP 47 primary-language subtag, e.g. en, es — UI language preference (§4.5)
- status
- last_login_at
- created_at
- updated_at
```

```text
roles
- id
- account_id
- name
- description
```

```text
user_role_assignments
- id
- user_id
- role_id
- farm_id nullable
- house_id nullable
- flock_id nullable
- created_at
```

## 5.3 Scope rules

- Workers may create daily entries only for assigned houses/flocks.
- Sales users may create sales but cannot edit medication records.
- Vet users may create health/medication notes but cannot sell eggs or edit expenses.
- Owners may override locked records with audit reason.
- Financial reports are hidden unless the role allows them.

---

# 6. Unified Phase Plan

This is the single canonical roadmap.

## Phase 1.0 — MVP (shippable egg loop) — ✅ SHIPPED (2026-07-16, epic #13)

Goal: ship the smallest **end-to-end** egg loop that is genuinely useful — record what the hens produce, know what is in stock, sell it. Ship this before building anything below it.

The loop, in one line: **daily entry (production by grade) → egg lots → stock → sales order → FIFO allocation → stock decremented.**

Includes:

1. Single-farm login (JWT). Multi-tenant isolation stays in the schema and infrastructure — one default farm/account is seeded; scoped-role UI is deferred.
2. Minimal setup: farm/house/flock records (seed a default farm; flock CRUD).
3. Daily entry with states, capturing **egg production by sellable grade**.
4. **Egg-lot generation from daily entry** (production by grade → dated lots). This is the bridge the rest of the loop depends on.
5. Egg lots as the stock balance (`quantity_available`). No separate movement-ledger table yet.
6. Customers with name + phone (required) and optional email/address/note (reference-app shape — no balances/payments).
7. Generic sales orders + FIFO egg-lot allocation + sale decrements stock.
8. Restricted egg lots and sale blocking (medication-withdrawal flag on lot). Already built; keep.
9. Read/list surfaces for daily entries, current stock by grade, and orders.
10. **Web client (React + Vite SPA)** in `web/`, consuming the JSON API: login, daily-entry capture, stock view, customers, sales order (create → add items → confirm), and the list/history screens. This is what makes 1.0 shippable to a farmer — the API alone is not the product.

Explicitly **not** in 1.0 (moved down): payments, dashboard, core reports, expenses, feed/water tracking, mortality movement generation, audit-log UI, CSV export, inventory movement ledger, offline queue. Schema may reserve columns/paths, but no logic is built.

## Phase 1.1 — Egg loop hardening (formerly Phase 1 remainder)

Goal: turn the shippable loop into the fuller operational system originally scoped as Phase 1.

Includes:

1. Roles and scoped permissions (house/flock-level RBAC UI)
2. Product catalog with egg products (replace raw grade strings)
3. Egg **inventory movement ledger** (explicit movement rows + cached balances)
4. Feed inventory and feed usage
5. Water usage
6. Mortality/culling and bird movement generation
7. Expenses
8. Customer payments and balances
9. Dashboard
10. Core reports
11. Audit log for critical changes
12. CSV export / manual backup
13. i18n infrastructure (§4.5): externalized UI strings in the SPA, machine-readable API validation/error codes, `users.language` with farm-locale fallback. Ships English-only — no translations yet.

## Phase 1.5 — Egg product hardening

Goal: make the egg product safer and more operationally complete.

Includes:

1. Legacy import wizard
2. Inventory reconciliation
3. Alert center
4. Email digest for critical alerts
5. Packaging inventory
6. Additives/supplements
7. Vaccination records
8. Flock profitability allocation rules
9. Backup/recovery workflow
10. First non-English UI translation: **Spanish (`es`)** language pack on the Phase 1.1 i18n infrastructure (§4.5). Coverage: every catalog key translated, plus localized rendering of API validation/domain codes; UC-012 Phase 1.5 acceptance criteria apply.

## Phase 2 — Pullet / chicken raising

Goal: support raising chicks/pullets before laying or sale.

Includes:

1. Pullet development records
2. Weight trend reports
3. Transfer readiness
4. Pullet sales using generic products
5. Expanded vaccination schedules
6. Growth and uniformity dashboards

## Phase 3 — Broilers / meat birds

Goal: support meat bird growout and live bird sales.

Includes:

1. Broiler growth records
2. Average daily gain
3. Feed conversion ratio
4. Harvest readiness
5. Live bird sales allocations
6. Broiler profitability

## Phase 4 — Meat processing

Goal: support processed meat inventory and sales.

Includes:

1. Broiler harvest records
2. Meat lots
3. Meat inventory movements
4. Processing batches
5. Meat product sales allocation
6. Meat traceability reports

## Phase 5 — Breeder and hatchery

Goal: support parent stock, hatching eggs, incubation, chick lots, and chick sales.

Includes:

1. Breeder daily records
2. Male/female counts
3. Hatching egg lots
4. Fertility tests
5. Incubation batches
6. Chick lots
7. Chick sales allocations

---

# 7. Unified Sprint List

Sprints A–D delivered **Phase 1.0 (MVP)** — shipped 2026-07-16 (epic #13; per-item status tags below are historical). Everything after is Phase 1.1+.

## Sprint A — Foundation (MVP) — mostly built

- Single-farm login / JWT / refresh tokens — **built**
- Multi-tenant infrastructure (tenant stamp, isolation) — **built, kept dormant behind one default farm**
- Seed default farm/house; flock CRUD — flock aggregate built; **seed + CRUD endpoints missing**
- Idempotency middleware — **built**

## Sprint B — Daily entry (MVP) — mostly built

- Daily entry states, duplicate prevention (natural-key upsert) — **built**
- Record production endpoint — **built**
- **Egg production by sellable grade** — **missing** (only cracked/dirty/discarded counts today)
- Copy from yesterday — deferred to 1.1
- Read/list daily entries — **missing**

## Sprint C — Egg lots and stock (MVP) — the critical bridge

- **Egg-lot generation from daily entry (production by grade → dated lots)** — **MISSING, top priority**; `EggLot.Create` exists but is never called
- Egg-lot `quantity_available` as stock balance — **built**
- Stock-by-grade read endpoint — **missing**
- Egg inventory movement ledger + explicit traceability rows — deferred to 1.1

## Sprint D — Sales (MVP) — mostly built

- Customers (name/phone required, optional email/address/note) — **missing entity** (only `CustomerId` referenced)
- Generic sales orders + items — aggregate built; **create/add-item endpoints missing**
- FIFO egg-lot allocation, pessimistic lock — **built + concurrency-tested**
- Confirm-sale decrements stock — **built**
- Restricted-lot sale blocking (medication withdrawal) — **built**
- Payments — deferred (possibly indefinitely)

## Sprint UI — Web client (MVP) — not started

React + Vite SPA in `web/`, consuming the JSON API. Runs alongside the backend sprints; each screen lands once its API slice exists.

- SPA scaffold + auth (login, token storage/refresh) + API client + routing shell — **missing**
- Daily-entry capture screen (production by grade) — **missing** (needs Sprint B)
- Stock-by-grade view — **missing** (needs Sprint C)
- Customers + sales order flow (create → add items → confirm) — **missing** (needs Sprint D)
- List/history screens (daily entries, orders) — **missing** (needs read endpoints)

**↑ Ship Phase 1.0 here. ↓ Everything below is post-MVP.**

## Sprint E — Phase 1.1 operational fill

- **i18n infrastructure first** (externalized UI strings, API message codes, `users.language`, §4.5) — prerequisite for the rest of Sprint E/F UI work; includes retrofitting all existing Phase 1.0 screens to the catalog
- Roles and scoped assignments (RBAC UI)
- Product catalog + egg product mapping (replace grade strings)
- Egg inventory movement ledger + cached balances
- Feed/water usage, mortality movement generation
- Basic expenses
- Health/welfare basics

## Sprint F — Phase 1.1 reporting and audit

- Dashboard
- Core reports
- Customer payments and balances
- Audit-log screens
- Exports / manual backup

## Sprint G — Phase 1.5 hardening

- Legacy import
- Reconciliation
- Packaging inventory
- Additives/supplements
- Vaccination
- Alert center + email digest
- First non-English UI translation — Spanish (depends on Sprint E i18n infrastructure, §4.5)

---

# 8. Daily Entry

## 8.1 States

```text
draft
submitted
locked
manager_adjusted
voided
```

| State | Meaning | Who can edit |
|---|---|---|
| draft | Incomplete or copied from previous day | creator, manager |
| submitted | Official daily record | worker until cutoff, manager |
| locked | Closed after configured days | owner/admin only |
| manager_adjusted | Submitted record adjusted after review | manager/owner |
| voided | Cancelled but preserved | owner/admin |

Default lock:

```text
Daily entries lock after 7 farm-local days.
```

## 8.2 Uniqueness

```text
UNIQUE(account_id, farm_id, house_id, flock_id, date)
```

## 8.3 Daily entry table

```text
daily_entries
- id
- account_id
- farm_id
- house_id
- flock_id
- date
- status
- copied_from_entry_id nullable
- submitted_at nullable
- submitted_by nullable
- locked_at nullable
- locked_by nullable
- notes
- created_by
- created_at
- updated_at
```

## 8.5 Daily-entry speed acceptance criteria

The original "under 60 seconds" target is a binding Phase 1 UX acceptance criterion for a normal egg-only layer flock daily entry.

For QA, a normal egg-only daily entry means:

```text
production_model = egg
no medication event being entered
no new health event being entered
no inventory reconciliation
no manual egg-lot adjustment
```

Acceptance criteria:

1. From the dashboard or app launch with remembered farm context, Daily Entry is reachable in **no more than 2 taps/clicks**.
2. Farm, house, and flock are preselected from the user's last used context when allowed by permissions.
3. The egg-only quick entry path requires **no more than 12 editable inputs** before submit, excluding optional notes.
4. The form provides **Copy from Yesterday**.
5. The form distinguishes blank from zero.
6. A user can save draft or submit without leaving the screen.
7. A trained worker should be able to submit a normal egg-only daily entry in **60 seconds or less** during usability testing.

If a daily entry includes optional health events, medication, reconciliation, manual adjustments, or future broiler/breeder sections, the 60-second target does not apply.


## 8.4 Modular sections

### Core section

Shown for all flocks:

```text
Date
Farm
House
Flock
Feed used
Water used
Deaths
Culls
Notes
```

### Egg section

Shown when:

```text
production_model IN ('egg', 'mixed')
```

Fields:

```text
Egg quantities by grade
Cracked/dirty/discarded/internal-use eggs
Egg notes
```

### Weight section

Shown when:

```text
production_model IN ('meat', 'raising', 'mixed')
```

Fields:

```text
Sample count
Average weight
Uniformity %
Weight notes
```

### Breeding section

Shown when:

```text
production_model IN ('breeding', 'mixed')
```

Phase 5 fields:

```text
Hatching eggs
Settable eggs
Fertility notes
Male/female count notes
```

Phase 1 may display this section as disabled/future if a breeder flock is created.

---

# 9. Egg Production, Egg Lots, and Egg Inventory

## 9.1 Egg grades

```text
egg_grades
- id
- account_id
- farm_id
- name
- grade_type: size / quality / custom
- sort_order
- is_saleable
- is_default
- active
```

Suggested defaults:

```text
Small
Medium
Large
Jumbo
Cracked
Dirty
Soft Shell
Discarded
Internal Use
Seconds
```

## 9.2 Daily production by grade

```text
daily_egg_grade_entries
- id
- account_id
- daily_entry_id
- farm_id
- house_id
- flock_id
- date
- egg_grade_id
- quantity_in_eggs
- created_at
- updated_at
```

## 9.3 Egg lots

```text
egg_lots
- id
- account_id
- farm_id
- house_id
- flock_id
- production_date
- egg_grade_id
- location_id nullable
- quantity_produced
- quantity_available
- status: available / restricted / depleted / discarded
- restricted_until nullable
- restriction_reason nullable
- source_daily_entry_id
- created_at
- updated_at
```

## 9.4 Egg inventory movements

```text
egg_inventory_movements
- id
- account_id
- farm_id
- egg_lot_id
- movement_type: production / sale / adjustment / discard / internal_use / transfer / reconciliation / void
- quantity_delta
- unit: egg
- reference_type
- reference_id
- reason
- notes
- created_by
- created_at
```

## 9.5 Egg movement rules

| Event | Movement |
|---|---|
| Daily entry submitted | `production` movement per saleable lot |
| Sale confirmed | `sale` movement per egg allocation |
| Eggs discarded | `discard` movement |
| Inventory corrected | `reconciliation` movement |
| Entry voided | reversing `void` movement |
| Internal use | `internal_use` movement |

## 9.6 Traceability chain

The system must be able to answer:

```text
Sale
→ sales_order_item
→ sales_order_item_egg_allocations
→ egg_lot
→ flock
→ house
→ production_date
→ daily_entry
```

## 9.7 Egg unit conversions

Eggs are stored as individual eggs (§24), but products are sold in packed units
(dozen, tray, carton, case). The system must convert sale units to individual eggs
deterministically. There is no implicit fixed factor: a carton is 12, 18, or 30 eggs
depending on the market, so the factor is farm-configurable.

```text
egg_unit_conversions
- id
- account_id
- farm_id nullable            (null = account default; a farm row overrides it)
- unit_code: individual / dozen / flat / tray / carton / case / other
- eggs_per_unit integer       (individual = 1, dozen = 12, tray/flat = 30, ...)
- active
- created_at
- updated_at
```

Suggested defaults (farm may override):

```text
individual = 1
dozen      = 12
flat/tray  = 30
carton     = 12
case       = 360   (30 dozen; farm-specific — confirm at setup)
```

### Conversion rules

- `quantity_base` on a sales order item is always individual eggs:
  `quantity_base = quantity × eggs_per_unit`.
- Resolve `eggs_per_unit` at line creation and **snapshot it on the sales order item**
  (see §10.5 `base_unit_factor`). A later change to the farm's carton/case definition
  must not reinterpret existing orders — same immutability principle as §4.6.
- If the selected unit has no active conversion for the farm, block sale confirmation.
  Never guess a factor.
- KPI conversions (e.g. dozens produced for feed-cost-per-dozen, §19.3) use the same
  table: `dozens = total eggs / 12`.

## 9.8 Grading and packing assumption

### Phase 1 assumption

Phase 1 assumes eggs are **graded at collection**: the daily grade entry (§9.2) captures
the final grade of each egg. The lot's grade is fixed at production. This matches farms
that candle/sort during collection and keeps the walking skeleton simple.

Phase 1 does **not** model a separate post-collection grading or wash/pack shift, nor
moving eggs between grades after the lot exists.

### Reserved path — regrade / repack (Phase 1.5)

Farms that grade or pack in a later shift need to move eggs between lots/grades without
breaking traceability. Discarding from one lot and producing into another is **not**
allowed for this, because it corrupts the traceability chain (§9.6) and the discard
ledger. Instead, reserve a grade-transfer that nets to zero eggs and preserves the source
lot's origin chain:

```text
egg_grade_transfers
- id
- account_id
- farm_id
- source_egg_lot_id
- dest_egg_lot_id             (new or existing lot at the destination grade)
- quantity_in_eggs
- reason: regrade / repack / downgrade / candling / other
- created_by
- created_at
```

Saving a grade transfer generates paired egg inventory movements (§9.4), extending the
`movement_type` enum:

```text
source lot: movement_type = grade_out, quantity_delta = -quantity_in_eggs
dest lot:   movement_type = grade_in,  quantity_delta = +quantity_in_eggs
```

The destination lot links back to the source lot so `source_daily_entry_id` traceability
is preserved. Net egg count across the two movements is zero. This module ships with
packaging inventory in Phase 1.5 (§6), not in the Phase 1 skeleton.

---

# 10. Product Catalog and Sales

This is the canonical sales model. There is no egg-only `sales_order_items` table.

## 10.1 Products

```text
products
- id
- account_id
- farm_id nullable
- name
- product_type: egg / live_bird / meat / chick / pullet / manure / service / other
- default_unit: egg / dozen / tray / carton / case / bird / lb / kg / package / other
- default_price_cents nullable
- currency_code: inherited from farm when farm_id is set
- active
- notes
- created_at
- updated_at
```

## 10.2 Egg product mappings

Egg products map to egg grades.

```text
product_egg_grade_mappings
- id
- account_id
- product_id
- egg_grade_id
```

Example:

| Product | product_type | egg_grade |
|---|---|---|
| Large Eggs | egg | Large |
| Medium Eggs | egg | Medium |
| Cracked Eggs | egg | Cracked |

## 10.3 Customers

```text
customers
- id
- account_id
- farm_id
- name
- phone
- email
- address
- customer_type: retail / wholesale / store / restaurant / family / processor / other
- default_price_level
- active
- notes
- created_at
- updated_at
```

## 10.4 Sales orders

```text
sales_orders
- id
- account_id
- farm_id
- customer_id
- order_date
- currency_code: copied from farm at order creation
- currency_minor_unit: copied from farm at order creation
- status: draft / confirmed / delivered / paid / cancelled / voided
- delivery_method: pickup / delivery
- delivery_date nullable
- subtotal_cents
- discount_cents
- tax_cents
- total_cents
- amount_paid_cents
- balance_due_cents
- notes
- created_by
- created_at
- updated_at
```

## 10.5 Sales order items

Canonical schema:

```text
sales_order_items
- id
- account_id
- sales_order_id
- product_id
- product_type_snapshot
- quantity
- unit
- base_unit_factor          (eggs-per-unit snapshot at line creation; see §9.7)
- quantity_base
- unit_price_cents
- line_total_cents
- created_at
- updated_at
```

`quantity_base` stores the normalized quantity:

| product_type | quantity_base means |
|---|---|
| egg | individual eggs |
| live_bird | individual birds |
| chick | individual chicks |
| pullet | individual birds |
| meat | base weight unit |
| manure | configured base unit |
| service | configured base unit or 1 line item |

## 10.6 Egg allocations

Egg products allocate from egg lots.

```text
sales_order_item_egg_allocations
- id
- account_id
- sales_order_item_id
- egg_lot_id
- quantity_in_eggs
- allocation_method: fifo / manual
- egg_inventory_movement_id
- created_at
```

## 10.7 Future live bird allocations

Reserved for future live bird sales.

```text
sales_order_item_bird_allocations
- id
- account_id
- sales_order_item_id
- flock_id
- bird_inventory_movement_id
- quantity_in_birds
- allocation_method: manual / fifo
- created_at
```

## 10.8 Future meat allocations

Reserved for future processed meat sales.

```text
sales_order_item_meat_allocations
- id
- account_id
- sales_order_item_id
- meat_lot_id
- quantity_weight
- unit
- meat_inventory_movement_id
- allocation_method: fifo / manual
- created_at
```

## 10.9 Sale confirmation transaction

When a sale is confirmed:

1. Validate the sales order is in a confirmable state.
2. For each `sales_order_item`, inspect `product_type_snapshot`.
3. If `product_type = egg`:
   - Resolve mapped egg grade from `product_egg_grade_mappings`.
   - Allocate from available egg lots using FIFO by production date unless manual allocation is used.
   - Block restricted egg lots.
   - Create `sales_order_item_egg_allocations`.
   - Create `egg_inventory_movements` with `movement_type = sale`.
   - Update cached egg lot balances.
4. If `product_type = live_bird`, `chick`, or `pullet`:
   - Phase 1: reject confirmation unless the future module is enabled.
   - Future: create bird allocation and bird inventory movement.
5. If `product_type = meat`:
   - Phase 1: reject confirmation unless the future module is enabled.
   - Future: allocate from meat lots and create meat inventory movement.
6. If `product_type = service` or non-stock item:
   - No inventory allocation required.
7. Update order totals and customer balance.
8. Write audit log.

## 10.9.1 Egg-lot allocation concurrency rule

Sale confirmation must serialize allocation against the selected egg lots.

Phase 1 rule:

```text
Within the sale confirmation transaction, lock candidate egg_lots rows before checking quantity_available and creating sale movements.
```

Recommended implementation:

```sql
SELECT *
FROM egg_lots
WHERE id IN (...)
FOR UPDATE;
```

Then:

1. Re-read `quantity_available` after the lock.
2. Validate the requested allocation still fits.
3. Create `sales_order_item_egg_allocations`.
4. Create `egg_inventory_movements`.
5. Update `egg_lots.quantity_available`.
6. Commit.

If the database does not support row-level locks, use optimistic concurrency:

```text
egg_lots.version integer
```

and require the update to match the expected version.

If the allocation no longer fits, the confirmation fails and the user must refresh allocations.

## 10.10 Withdrawal rule for egg sales

Default:

```text
Restricted egg lots cannot be sold.
```

Owner-only override may be added later, with mandatory audit reason.

Phase 1 should hard-block restricted egg lots.

## 10.11 Payments

```text
payments
- id
- account_id
- farm_id
- sales_order_id
- customer_id
- payment_date
- amount_cents
- currency_code: copied from sales_order
- currency_minor_unit: copied from sales_order
- method: cash / check / card / bank_transfer / mobile_payment / other
- reference_number
- notes
- created_by
- created_at
```

---

# 11. Live Bird Inventory and Mortality

## 11.1 Bird inventory movements

```text
bird_inventory_movements
- id
- account_id
- farm_id
- house_id nullable
- flock_id
- movement_type: placement / mortality / cull / transfer_in / transfer_out / sale / harvest / adjustment / depletion / void
- quantity_delta
- movement_date
- reference_type
- reference_id
- reason
- notes
- created_by
- created_at
```

## 11.2 Placement rule

When a flock is created:

```text
movement_type = placement
quantity_delta = starting_bird_count
reference_type = flock
reference_id = flock_id
```

## 11.3 Mortality records

```text
mortality_records
- id
- account_id
- daily_entry_id nullable
- farm_id
- house_id
- flock_id
- date
- death_count
- cull_count
- reason: disease / injury / predator / heat_stress / weak / unknown / other
- description
- disposal_method
- created_by
- created_at
- updated_at
```

Saving mortality creates bird inventory movements:

```text
death_count → movement_type = mortality, quantity_delta = -death_count
cull_count → movement_type = cull, quantity_delta = -cull_count
```

## 11.4 Current live birds

```text
Current live birds = sum(bird_inventory_movements.quantity_delta)
```

Phase 1 may display this alongside:

```text
starting birds - deaths - culls +/- transfers
```

But the bird ledger is canonical.

---

# 12. Feed, Water, and General Inventory

## 12.1 Inventory items

```text
inventory_items
- id
- account_id
- farm_id
- name
- category: feed / supplement / additive / medication / vaccine / packaging / bedding / sanitation / equipment_part / other
- unit
- supplier_id nullable
- default_cost_cents
- requires_lot_tracking
- requires_expiry_tracking
- requires_withdrawal_tracking
- storage_notes
- active
- created_at
- updated_at
```

## 12.2 Inventory lots

```text
inventory_lots
- id
- account_id
- farm_id
- inventory_item_id
- lot_number nullable
- expiry_date nullable
- received_date
- quantity_received
- quantity_available
- unit_cost_cents
- supplier_id nullable
- storage_location
- created_at
- updated_at
```

## 12.3 Inventory movements

```text
inventory_movements
- id
- account_id
- farm_id
- inventory_lot_id
- inventory_item_id
- movement_type: purchase / usage / adjustment / transfer / discard / reconciliation / void
- quantity_delta
- unit
- house_id nullable
- flock_id nullable
- reference_type
- reference_id
- reason
- notes
- created_by
- created_at
```

## 12.4 Feed usage

```text
feed_usage
- id
- account_id
- daily_entry_id nullable
- farm_id
- house_id
- flock_id
- inventory_item_id
- inventory_lot_id nullable
- usage_date
- quantity_used
- unit
- estimated_cost_cents
- costing_method: lot_cost / latest_lot / average_cost / manual
- notes
- created_by
- created_at
```

Saving feed usage creates `inventory_movements` with `movement_type = usage`.

## 12.5 Water usage

```text
water_usage
- id
- account_id
- daily_entry_id nullable
- farm_id
- house_id
- flock_id
- usage_date
- water_quantity
- unit: gallons / liters
- water_source: well / municipal / tank / other
- meter_start nullable
- meter_end nullable
- notes
- created_by
- created_at
```

## 12.6 Water-to-feed ratio

```text
Water-to-feed ratio = normalized water quantity / normalized feed quantity
```

---

# 13. Medication, Withdrawal, Vaccines, and Additives

## 13.1 Medication applications

```text
medication_applications
- id
- account_id
- farm_id
- house_id
- flock_id
- inventory_item_id
- inventory_lot_id nullable
- start_date
- end_date nullable
- status: planned / active / completed / cancelled
- dosage_amount
- dosage_unit
- dosage_basis
- delivery_method: water / feed / injection / direct / other
- reason
- diagnosis
- authorized_by
- egg_withdrawal_until nullable
- notes
- created_by
- created_at
- updated_at
```

## 13.2 Active withdrawal

Withdrawal is active when:

```text
egg_withdrawal_until IS NOT NULL
AND egg_withdrawal_until >= farm_today
```

Medication save transaction:

1. Create medication application.
2. Create inventory movement usage if a stock item is selected.
3. Update `flocks.active_egg_withdrawal_until`.
4. Create withdrawal alert.
5. Audit log.

## 13.3 Egg lot restriction

When egg lots are generated:

```text
if flock.active_egg_withdrawal_until >= production_date:
    egg_lot.status = restricted
    egg_lot.restricted_until = flock.active_egg_withdrawal_until
```

## 13.4 Vaccination records

```text
vaccination_records
- id
- account_id
- farm_id
- house_id
- flock_id
- vaccine_item_id
- inventory_lot_id nullable
- date_administered
- disease_target
- method
- birds_vaccinated
- administered_by
- next_due_date nullable
- notes
- created_by
- created_at
```

## 13.5 Additive applications

```text
additive_applications
- id
- account_id
- farm_id
- house_id
- flock_id
- inventory_item_id
- inventory_lot_id nullable
- application_date
- start_date nullable
- end_date nullable
- delivery_method: water / feed / direct / spray / other
- dosage_amount
- dosage_unit
- dosage_basis
- quantity_used
- reason
- notes
- created_by
- created_at
```

Vaccination and additive records create inventory usage movements when stock is selected.

---

# 14. Health, Welfare, and Beak Treatment

## 14.1 Health events

```text
health_events
- id
- account_id
- farm_id
- house_id
- flock_id
- event_date
- event_type: symptom / diagnosis / vet_visit / lab_test / injury / welfare_issue / other
- severity: low / medium / high / critical
- symptoms_text
- diagnosis
- action_taken
- vet_name
- follow_up_date nullable
- notes
- created_by
- created_at
```

## 14.2 Welfare tags

```text
welfare_observation_tags
- id
- account_id
- health_event_id
- observation_type
```

Default tags:

```text
feather_pecking
cannibalism
lameness
stress_behavior
poor_shell_quality
dirty_eggs
wet_litter
low_feed_intake
low_water_intake
predator_incident
```

## 14.3 Beak treatment

Phase 2 module.

```text
beak_treatment_records
- id
- account_id
- farm_id
- house_id
- flock_id
- date_performed
- bird_age_days
- birds_treated
- method: infrared / hot_blade / other
- trim_type: first_trim / second_trim / corrective / supplier_performed
- upper_beak_amount
- lower_beak_amount
- operator_name
- equipment_used
- reason
- immediate_mortality
- notes
- created_by
- created_at
```

---

# 15. Weight and Growth Records

Weight tracking is generic.

```text
flock_weight_records
- id
- account_id
- farm_id
- house_id
- flock_id
- date
- sample_count
- average_weight
- unit: g / kg / oz / lb
- uniformity_percent nullable
- notes
- created_by
- created_at
```

Used for:

- Pullets
- Broilers
- Breeders
- Optional layer monitoring

Future broiler KPIs:

```text
Average daily gain
Feed conversion ratio
Target harvest weight
Harvest readiness
Cost per weight unit
```

---

# 16. Expenses and Profitability

```text
expenses
- id
- account_id
- farm_id
- house_id nullable
- flock_id nullable
- supplier_id nullable
- expense_category_id
- expense_date
- description
- amount_cents
- currency_code: copied from farm at expense creation
- currency_minor_unit: copied from farm at expense creation
- allocation_method: direct / bird_count_share / revenue_share / manual
- payment_method
- receipt_attachment_id nullable
- notes
- created_by
- created_at
```

```text
expense_categories
- id
- account_id
- farm_id nullable
- name
- active
```

Direct expenses are assigned to the flock/house/farm. Shared expenses are allocated by bird count, revenue share, or manual percentage.

---

# 17. Alerts, Tasks, Reconciliation, Import, and Backup

## 17.1 Alerts

```text
alerts
- id
- account_id
- farm_id
- house_id nullable
- flock_id nullable
- alert_type
- severity: info / warning / critical
- message
- triggered_at
- status: unread / read / dismissed / resolved
- related_record_type
- related_record_id
- created_at
```

Important alert types:

- Medication withdrawal active
- Restricted egg lots available
- Feed stock low
- Egg production drop
- Mortality spike
- Water intake drop
- Customer payment overdue
- Inventory reconciliation needed
- Daily entry missing
- Locked entry override
- Expiring medication/vaccine lot

## 17.2 Tasks

```text
tasks
- id
- account_id
- farm_id
- house_id nullable
- flock_id nullable
- title
- description
- due_date
- assigned_to
- priority
- status: open / completed / cancelled
- recurrence_rule nullable
- completed_at nullable
- created_by
- created_at
```

## 17.3 Inventory reconciliation

Reconciliation creates movement records.

```text
movement_type = reconciliation
quantity_delta = physical_count - system_count
reason required
```

## 17.4 Legacy import

Import wizard supports:

- Existing flocks with initial bird counts
- Historical egg production
- Historical mortality
- Customers
- Sales history
- Feed purchases
- Expenses
- Starting inventory counts

Imported records include:

```text
import_batch_id
is_backdated = true
source = legacy_import
```

## 17.5 Backup/recovery

Phase 1:

- Manual CSV export
- Full account export
- Database dump instructions for self-hosted deployment

Phase 1.5:

- Scheduled backup
- Storage target
- Backup health checks
- Rollback failed imports

---

# 18. Audit Log

```text
audit_logs
- id
- account_id
- user_id
- action: create / update / delete / submit / lock / unlock / override / import / export / void
- entity_type
- entity_id
- old_values_json
- new_values_json
- reason nullable
- ip_address nullable
- created_at
```

Audit log required for:

- Daily entry edits after submission
- Locked entry overrides
- Withdrawal overrides
- Inventory reconciliation
- Sales voids
- Expense changes
- User/role changes
- Import/export

---

# 19. Reports and KPIs

## 19.1 Phase 1 reports

- Daily production report
- Weekly production report
- Egg inventory by grade and age
- Egg lot traceability report
- Sales summary
- Customer balance report
- Feed usage report
- Mortality report
- Expense summary
- Basic profit report

## 19.2 Traceability report

```text
Sale
→ sales_order_item
→ sales_order_item_egg_allocations
→ egg_lot
→ flock
→ house
→ daily_entry
```

## 19.3 KPI formulas

```text
Current live birds =
sum(bird_inventory_movements.quantity_delta)
```

```text
Hen-day production % (single day) =
eggs collected on date / hen-days on date × 100

Hen-day production % (period) =
total eggs collected in period / sum(daily hen-days in period) × 100
```

`hen-days on date` is the live laying-bird count **on that operational date**, reconstructed
from the bird ledger, not the current count:

```text
hen-days on date =
sum(bird_inventory_movements.quantity_delta) WHERE movement_date <= date
  AND flock.production_purpose = 'layer'
  AND flock.sex = 'female'
```

Rules:

- Use the point-in-time ledger count on the entry date, never the current live count.
  Using the current (post-mortality) count inflates the percentage.
- Count laying **females only**. Hen-day is a female-bird metric by definition. Exclude
  males and non-layer flocks.
- **Mixed-sex flocks (`sex = 'mixed'`) overcount** because the ledger tracks a single
  `starting_bird_count` with no female subcount, so males inflate the denominator. Phase 1
  options:
  - Prefer modelling a laying flock as `sex = 'female'` so the metric is exact, or
  - Flag hen-day % as **approximate** for `mixed` flocks, and reserve a future
    `female_bird_count` (or sex-split placement movements) to make it exact.
  Do not silently report an exact-looking hen-day % for a mixed flock.
- Period rate divides total eggs by the sum of daily hen-days, not by an end-of-period
  headcount.
- The benchmark-meaningful period anchors to **start of lay** (`flock.expected_start_lay_date`,
  or first laying production date), not an arbitrary calendar range. Comparing week 1 of
  lay against week 20 as one blended rate produces a number that matches no industry
  layer-curve benchmark. Report hen-day % by flock age / week-of-lay for comparability.

```text
Saleable egg % =
saleable eggs / total eggs collected × 100
```

```text
Egg loss % =
non-saleable eggs / total eggs collected × 100
```

```text
Feed cost per dozen =
feed cost / dozens produced
```

```text
Water-to-feed ratio =
normalized water quantity / normalized feed quantity
```

```text
Profit =
egg revenue - allocated expenses
```

---

# 20. Use Cases

## UC-001 Create account

**Actor:** Owner
**Goal:** Create tenant root.

**Flow:**

1. Owner signs up.
2. System creates account.
3. Owner creates first farm.
4. Owner becomes account owner.

**Acceptance criteria:**

- Account exists before farm creation.
- Farm has `account_id`.
- Owner has full permissions.

## UC-010 Create farm, house, and flock

**Actor:** Owner/Manager
**Goal:** Set up production structure.

**Flow:**

1. Create farm.
2. Create house.
3. Create flock.
4. Select species, production purpose, production model, and sex.
5. Enter starting bird count and placement date.
6. System creates bird placement movement.

**Acceptance criteria:**

- Flock belongs to account, farm, and house.
- Flock classification is valid.
- Bird placement movement exists.


## UC-011 Configure farm localization

**Actor:** Owner/Manager
**Goal:** Tie a farm to a currency, locale, and timezone.

**Flow:**

1. User creates or edits a farm.
2. User selects timezone.
3. User selects locale.
4. User selects currency code.
5. System derives currency symbol and minor unit where possible.
6. System saves settings.
7. Daily entry and reports use the farm timezone.
8. Sales, expenses, payments, and prices use the farm currency.
9. UI formats numbers, dates, and money using the farm locale.

**Acceptance criteria:**

- Farm cannot be active without timezone, locale, and currency code.
- Daily Entry defaults to the farm-local date.
- Sales orders copy the farm currency code and minor unit at creation.
- Expenses copy the farm currency code and minor unit at creation.
- Payments copy the sales order currency code and minor unit.
- Financial reports use row-level currency code and minor unit.
- Changing farm currency is blocked after sales, payments, or expenses exist.
- Cross-farm financial aggregation is disabled or clearly marked when currencies differ.


## UC-012 Select UI language

**Actor:** Any user (including Read-only — self-service preference, not a farm-scoped permission)
**Goal:** Read the UI in the user's preferred language.
**Phase:** 1.1 (infrastructure, English-only) / 1.5 (first translation)

**Flow:**

1. User opens their profile/settings.
2. User selects a language from the available language packs. (While only English exists, the selector may be hidden or disabled.)
3. System saves `users.language` (BCP 47 primary-language subtag, §4.5).
4. UI re-renders strings from the catalog resolved per §4.5.
5. Numbers, dates, and money continue to format per the farm locale (§4.5).

**Acceptance criteria — Phase 1.1 (infrastructure):**

- All UI strings are served from the English catalog; no hardcoded strings in screens.
- Login/current-user response includes `language`; the SPA resolves the catalog before first paint.
- Any authenticated user can update their own `users.language`; changing it never affects other users or farm settings.
- API validation and error responses carry stable machine-readable codes (§4.5); the client renders text from the code with the message as English fallback.
- Formatting (§4.5 display rule) is unaffected by UI language choice.

**Acceptance criteria — Phase 1.5 (first translation):**

- Catalog resolution follows §4.5: user language → default-farm locale language → English; unsupported stored values are treated as unset.
- A missing translation key falls back to the English string, never a blank or raw key.
- The language selector is visible and lists all shipped language packs.


## UC-020 Create daily entry draft

**Actor:** Worker
**Goal:** Start a daily entry.

**Flow:**

1. Worker opens Daily Entry.
2. System defaults last farm/house/flock.
3. Sections are shown based on flock production model.
4. Worker clicks Copy from Yesterday or enters values manually.
5. Worker saves draft.

**Acceptance criteria:**

- Draft does not create inventory movements.
- Duplicate drafts for same farm/house/flock/date are prevented.
- Blank values are distinct from zero.

## UC-021 Submit daily entry

**Actor:** Worker/Manager
**Goal:** Submit official daily record.

**Flow:**

1. User reviews draft.
2. System validates required fields.
3. User submits.
4. Egg entries create egg lots and production movements.
5. Feed entries create feed usage and inventory movements.
6. Water entries create water records.
7. Mortality/culls create bird movements.
8. KPIs and alerts update.

**Acceptance criteria:**

- Submission is idempotent.
- Ledger side effects happen exactly once.
- Restricted egg lots are generated if flock withdrawal is active.
- Audit log records submission.

## UC-030 Sell egg product

**Actor:** Sales user/Manager
**Goal:** Sell eggs using the product-generic sales model.

**Flow:**

1. User creates sales order.
2. User selects customer.
3. User adds product, such as `Large Eggs`.
4. System reads `product_type = egg`.
5. System resolves egg grade through `product_egg_grade_mappings`.
6. System suggests FIFO egg lot allocations.
7. User confirms allocations.
8. System blocks restricted lots.
9. User confirms sale.
10. System creates `sales_order_item_egg_allocations`.
11. System creates egg inventory sale movements.
12. Customer balance updates.

**Acceptance criteria:**

- `sales_order_items` references `product_id`.
- Egg sale lines allocate from egg lots.
- Restricted egg lots cannot be sold.
- Traceability report works.

## UC-031 Manually allocate egg lots

**Actor:** Sales user/Manager
**Goal:** Choose specific lots for egg sale.

**Flow:**

1. User opens allocation panel for an egg product line.
2. User selects egg lots by grade, date, flock, and location.
3. System validates available quantity.
4. System blocks restricted lots.
5. User confirms allocation.

**Acceptance criteria:**

- Allocation cannot exceed available lot quantity.
- Manual allocation is stored as `allocation_method = manual`.
- Allocation records use `sales_order_item_egg_allocations`.

## UC-032 Sell non-egg product later

**Actor:** Sales user/Manager
**Goal:** Sell future products such as live birds, pullets, meat, manure, or services.

**Phase:** Future

**Flow:**

1. User adds product to sales order.
2. System checks `product_type_snapshot`.
3. If product type is not enabled, system prevents confirmation.
4. If module is enabled, system uses that product type’s allocation table.
5. Customer balance updates.

**Acceptance criteria:**

- Sales table does not need redesign.
- Product-specific allocation is modular.
- Egg allocation remains unchanged.

## UC-040 Record medication with egg withdrawal

**Actor:** Manager/Vet
**Goal:** Track treatment and prevent unsafe egg sales.

**Flow:**

1. User selects flock.
2. Enters medication and dates.
3. Enters egg withdrawal date.
4. System creates medication record.
5. System updates flock withdrawal cache.
6. Future egg lots produced during withdrawal become restricted.

**Acceptance criteria:**

- Restricted egg lots are sale-blocked.
- Audit log records medication creation/edit.

## UC-050 Record weight

**Actor:** Worker/Manager
**Goal:** Record average bird weight.

**Flow:**

1. User opens daily entry or flock weight screen.
2. Selects flock.
3. Enters sample count, average weight, unit, and uniformity.
4. System saves weight record.

**Acceptance criteria:**

- Weight records link to account/farm/house/flock.
- Section appears for meat, raising, and mixed production models.
- Future broiler reports can use the record.

---

# 21. Wireframe Coverage

## 21.1 Current v4 wireframes

These wireframes are current for the v4 product-generic/extensible model:

1. Dashboard
2. Modular Daily Entry
3. Flock Setup
4. Products
5. Generic Sales Order
6. Sales Allocation by Product Type
7. Egg Sales Allocation
8. Live Bird Ledger
9. Growth / Weight Records
10. Future Meat Module
11. Reports
12. Farm Localization Settings

## 21.2 Carried forward from v2 unchanged

These v2 wireframes remain valid because they do not conflict with the v4 sales schema:

1. Login
2. Account / Farm Setup
3. Flocks
4. Flock Detail
5. Egg Production
6. Egg Lots / Traceability
7. Egg Inventory
8. Customers
9. Feed & Inventory
10. Inventory Reconciliation
11. Water & Additives
12. Health & Welfare
13. Medication & Withdrawal
14. Beak Treatment
15. Expenses
16. Alerts & Tasks
17. Users & Roles
18. Audit Log
19. Legacy Import
20. Settings — now also carries the per-user UI language selector (UC-012, §4.5)
21. Mobile Daily Entry

## 21.3 Superseded v2 wireframes

These v2 wireframes are superseded and should not be used:

1. `sales_order.svg`
2. `sales_allocation.svg`

They were replaced because sales are now product-generic.

## 21.4 Wireframe intent

Wireframes are low-fidelity. They define layout intent, major controls, and data relationships. They are not final visual design.

---

# 22. Transaction Boundaries

The following must be atomic database transactions:

- Submit daily entry
- Edit submitted daily entry
- Confirm sale
- Void sale
- Record medication with withdrawal
- Save feed usage
- Reconcile inventory
- Import batch
- Create flock with initial bird placement movement

---

# 23. Idempotency

Submitting the same draft twice must not duplicate lots or movements.

Use uniqueness around:

```text
reference_type + reference_id + movement_type
```

and table-specific constraints.

---

# 24. Data Storage Rules

## Money

Store money as integer minor units using the selected farm's currency.

The existing field suffix `_cents` is acceptable for Phase 1 when most deployments use 2-decimal currencies, but the meaning is:

```text
amount_cents integer = amount in farm.currency_code minor units
```

Future code may rename these fields to `_minor_units` for precision.

## Egg quantities

Store eggs as individual eggs.

```text
quantity_in_eggs integer
```

## Live bird quantities

Store birds as individual birds.

```text
quantity_in_birds integer
```

## Meat quantities

Store weight in normalized base unit.

```text
quantity_weight
unit
```

## User language

Store the UI language preference as a nullable BCP 47 primary-language subtag.

```text
users.language varchar nullable, e.g. en, es — NULL means "not set" (resolve per §4.5)
```

No index needed; read at login. Unsupported stored values are tolerated, not rejected (§4.5).

## Soft delete

Do not physically delete business records in normal use.

Use statuses:

```text
voided
cancelled
inactive
depleted
```

---

# 25. Final v4 Summary

The canonical operational loop is:

```text
Daily Entry
→ Egg Production by Grade
→ Egg Lots
→ Egg Inventory Movements
→ Product-Generic Sales Order
→ Egg Allocation
→ Traceability Report
```

The canonical future expansion loop is:

```text
Flock Classification
→ Modular Daily Entry
→ Product Catalog
→ Product-Specific Allocation
→ Bird Ledger / Meat Lots / Hatchery Lots
```

The most important rules:

```text
Sales are generic.
Egg inventory remains lot-traceable.
Future poultry modules plug into product-specific allocation tables.
Financial rows use their own stored currency fields forever.
Egg lot allocation is serialized during sale confirmation.
```
