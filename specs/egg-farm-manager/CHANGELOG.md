# Egg Farm Manager v4.2 — Build-Readiness Patch

## Purpose

v4.2 applies small additive clarifications from the Opus build-readiness review. It does not redesign the system or pull deferred features into Phase 1.

## 1. Financial row currency immutability

Added explicit rules:

- Money display and financial reports must use each row's stored `currency_code` and `currency_minor_unit`.
- Financial displays must not reinterpret historical rows using the farm's current currency.
- `sales_orders`, `payments`, and `expenses` now snapshot both `currency_code` and `currency_minor_unit`.
- Changing `farms.currency_code` is blocked once any sales order, payment, or expense exists for that farm.

## 2. Currency derivation fallback

Added fallback rules:

- If currency symbol cannot be derived, display the ISO 4217 currency code.
- If minor unit cannot be derived, default to `2`.
- Phase 1 may use a static ISO 4217 lookup table.

## 3. Measurable daily-entry UX acceptance criteria

Promoted the 60-second daily-entry target into testable criteria:

- Daily Entry reachable in no more than 2 taps/clicks from remembered context.
- Farm/house/flock preselected when allowed.
- Egg-only quick entry path requires no more than 12 editable inputs before submit.
- Copy from Yesterday is available.
- Blank and zero are distinct.
- Save Draft and Submit do not require leaving the screen.
- 60-second target applies only to normal egg-only daily entry.

## 4. Egg-lot allocation concurrency

Added sale-confirmation concurrency rule:

- Lock candidate `egg_lots` rows during allocation, e.g. `SELECT ... FOR UPDATE`.
- Revalidate `quantity_available` after locking.
- If row locks are unavailable, use optimistic concurrency with `egg_lots.version`.
