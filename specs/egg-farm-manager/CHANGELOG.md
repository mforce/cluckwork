# Egg Farm Manager v2 — Feedback Integration Changelog

## Integrated from DeepSeek V4 Pro

- Added explicit sales-to-inventory bridge.
- Added inventory movement side effects for sales, feed usage, additives, medications, and vaccines.
- Replaced grade-only egg inventory with traceable egg lots.
- Added sales order item allocations to allow pooled inventory with traceability.
- Added daily entry states: draft, submitted, locked, manager_adjusted, voided.
- Added unique daily entry constraint.
- Defined farm-local date handling.
- Added legacy import wizard.
- Added alert delivery plan and alert center.
- Added house/flock-level RBAC scoping.
- Added account/tenant decision.
- Added expense allocation rules.
- Added configurable alert thresholds.
- Added reconciliation workflow.
- Added backup and recovery section.

## Integrated from Sonnet 4.6

- Added account/company root table.
- Reduced Phase 1 scope to a walking skeleton.
- Improved daily entry UX requirements with remembered context and Copy from Yesterday.
- Added missing wireframe coverage list.
- Added structured welfare observation tags.
- Added cached `flocks.active_egg_withdrawal_until`.
- Added water-to-feed ratio KPI.
- Clarified ledger source of truth + cached balances.
- Documented recompute-on-read for mortality/live-bird counts.
- Added traceability-first sales allocation workflow.

## Design choice where v2 differs slightly from reviewers

Both reviewers suggested making `flock_id` required directly on sales order items.  
v2 instead uses `egg_lots` and `sales_order_item_allocations`.

Reason:

A real sale may pull from multiple flocks and production dates.  
Egg lots preserve traceability while allowing pooled grade inventory.
