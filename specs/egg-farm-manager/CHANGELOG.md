# Egg Farm Manager v4.1 — Farm Localization Patch

## Change

Farm is now explicitly tied to:

```text
currency_code
locale
timezone
```

## Why

For the current product, the farm is the operational unit. Daily entries, sales, expenses, reports, and display formatting should use the selected farm's settings.

## Added / clarified

### farms table

Added:

```text
timezone
locale
currency_code
currency_symbol
currency_minor_unit
first_day_of_week
date_format_override
time_format_override
```

### Business rules

- Phase 1 is single-currency per farm.
- No exchange-rate conversion in Phase 1.
- Sales orders copy farm currency at creation.
- Expenses copy farm currency at creation.
- Payments copy sales order currency.
- UI formats money/dates/numbers using farm locale.
- Operational dates use farm timezone.
- Audit timestamps remain UTC.
- Cross-farm financial aggregation should be disabled or clearly marked when farm currencies differ.

### Use case

Added:

```text
UC-011 Configure farm localization
```

### Wireframes

Added / updated:

```text
farm_localization.svg
```
