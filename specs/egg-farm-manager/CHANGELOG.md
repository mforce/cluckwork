# Egg Farm Manager v3 — Changelog

## Why this version exists

v2 was excellent for an egg-producing farm, but several core structures were still egg-specific. v3 keeps the egg MVP intact while making the design future-proof for chicken raising, broilers, live bird sales, and meat production.

## Changes made

- Added flock classification: `species`, `production_purpose`, `production_model`, `sex`, and `expected_harvest_date`.
- Added generic `products` table.
- Changed sales order items to reference `product_id` instead of being egg-only.
- Added egg product to egg grade mapping.
- Kept egg-lot allocation for egg products.
- Reserved future allocation tables for live birds and meat products.
- Added `bird_inventory_movements`.
- Added `flock_weight_records`.
- Changed daily entry concept to modular sections by flock type.
- Added future module reservations for pullets, broilers, meat lots, breeders, and hatchery.
- Added v3-specific wireframes for products, generic sales, flock setup, bird ledger, weight records, and future meat module.
