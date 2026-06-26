# Egg Farm Manager v4 — Reconciliation Changelog

## Fixed from Opus 4.8 review

### 1. Sales schema conflict fixed

The egg-only `sales_order_items` schema was removed. The product-generic schema is now canonical:

```text
sales_order_items.product_id
sales_order_items.product_type_snapshot
sales_order_items.quantity
sales_order_items.quantity_base
```

Egg sales allocate through:

```text
sales_order_item_egg_allocations
```

The old v2 `sales_order_item_allocations` name is no longer canonical.

### 2. Phase plans merged

The v2 hardening roadmap and v3 species roadmap are merged into one ordered plan:

1. Phase 1 — Egg farm walking skeleton
2. Phase 1.5 — Egg product hardening
3. Phase 2 — Pullet / chicken raising
4. Phase 3 — Broilers / meat birds
5. Phase 4 — Meat processing
6. Phase 5 — Breeder and hatchery

There is also one sprint list.

### 3. Wireframe coverage clarified

The spec now states:

- Current v4 wireframes
- v2 wireframes carried forward unchanged
- v2 sales wireframes superseded

The sales wireframes were redrawn around the product-generic sales model.

### 4. Enum/validity gaps closed

Added a validity table for:

```text
production_purpose × production_model
```

Also clarified daily-entry section visibility for:

```text
egg
meat
raising
breeding
mixed
```

### 5. Preserved correct v2/v3 design

Preserved:

- Egg-lot traceability
- Lot-level withdrawal restriction
- Ledger-first inventory
- Product-specific allocation tables
- Bird movement ledger
- Weight records
- Modular daily entry
- Account/tenant root
- Daily-entry state machine
- Permission model
