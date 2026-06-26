# Egg Farm Manager — Product & Technical Specification v2

**Product:** Egg Farm Manager  
**Domain:** Poultry egg-producing farm management  
**Version:** v2  
**Purpose:** Build a practical, traceable, ledger-based system for managing layer farms, egg production, egg inventory, feed usage, flock health, sales, and profitability.

---

## 0. v2 Summary

This version incorporates two technical review passes and tightens the system around build-critical decisions:

1. **Egg traceability is mandatory.** Egg inventory is tracked by **egg lots**, not grade-only totals.
2. **Inventory uses immutable movement ledgers.** Cached balances are allowed only as rebuildable read models.
3. **Sales allocate from egg lots.** A sale line may pull from multiple flocks/dates/lots.
4. **Medication withdrawal is enforced at egg-lot and sale-allocation level.**
5. **Daily entry has states:** `draft`, `submitted`, `locked`, `manager_adjusted`.
6. **Concurrency protection is defined.**
7. **An account/tenant root exists from day one.**
8. **Phase 1 is trimmed to a true walking skeleton.**
9. **Wireframes now include missing administrative, import, reconciliation, alert, and traceability screens.**

---

# 1. Product Goal

Build a system that lets an egg-producing poultry farm answer, with confidence:

- How many eggs were produced today?
- Which flock/house produced them?
- Which egg lots are available for sale?
- Are any eggs restricted due to medication withdrawal?
- How much feed was consumed and what did it cost?
- What is the flock’s hen-day production?
- What is the egg loss rate?
- What is available inventory by grade, age, flock, and location?
- What did we sell, to whom, and from which egg lots?
- What is the farm’s profit per period, per flock, and per dozen?
- What critical health, mortality, inventory, or withdrawal alerts require action?

The system is optimized for **daily operational use**, not just reporting.

---

# 2. Explicit Non-Goals for Phase 1

Phase 1 will not include:

- Broiler production
- Hatchery management
- Breeder/parent-stock management
- Feed mill formulation
- Processing/slaughter plant management
- Offline mobile mode
- IoT/sensor automation
- Full accounting package replacement
- Payroll
- Delivery route optimization
- Advanced certification workflows

These can be future modules.

---

# 3. Architecture Decision Log

## 3.1 Deployment model

The system should support two paths:

1. **Self-hosted single account**
2. **Future SaaS / multi-account**

Therefore, the schema includes an `accounts` root entity from day one.

```text
Account / Tenant
  Users
  Farms
    Houses
      Flocks
```

Even if the MVP UI only shows one account, all farm data belongs to an account.

## 3.2 Inventory model

Inventory is **ledger-first**.

Rule:

```text
Never directly edit a stock balance.
Every stock change must create an inventory movement.
Cached balances are allowed only if they can be rebuilt from the movement ledger.
```

This applies to:

- Eggs
- Feed
- Supplements
- Medications
- Vaccines
- Packaging
- Bedding/litter
- Cleaning supplies
- Equipment parts

## 3.3 Egg inventory model

Egg inventory is **lot-based**, not just grade-based.

An egg lot is created from a submitted production entry.

```text
Egg Lot = farm + house + flock + production_date + grade + location + quantity
```

Dashboards may show aggregated totals:

```text
Large eggs: 42 trays
Medium eggs: 18 trays
```

But those are views over traceable lots.

## 3.4 Sales allocation model

A sales order item may be fulfilled by one or more egg lots.

Example:

```text
Sale line: 10 trays of Large eggs

Allocations:
- 4 trays from Flock A, produced June 24
- 3 trays from Flock B, produced June 25
- 3 trays from Flock A, produced June 25
```

This allows pooled inventory while preserving traceability.

## 3.5 Medication withdrawal model

Withdrawal enforcement is based on the egg lot.

If eggs are produced during a flock’s withdrawal window, the generated egg lot is restricted until the withdrawal date.

```text
egg_lots.restricted_until
egg_lots.restriction_reason
```

This is safer than checking only whether the flock is currently under withdrawal.

## 3.6 Date/time model

Operational records use **farm-local dates**.

Examples:

- Daily egg production date
- Feed usage date
- Sales date
- Mortality date

Audit timestamps use UTC.

```text
daily_entry.date = farm-local date
created_at / updated_at = UTC timestamp
```

---

# 4. Core Users and Permissions

## 4.1 User roles

| Role | Purpose |
|---|---|
| Owner | Full access to all modules and financials |
| Manager | Farm operations, inventory, health, reports, approvals |
| Worker | Daily entry for assigned houses/flocks |
| Sales | Customers, sales, payments, egg inventory view |
| Vet/Consultant | Health, medication, vaccination, welfare notes |
| Read-only | Dashboard and reports only |

## 4.2 Data scope

User access can be scoped by:

- Account
- Farm
- House
- Flock

Example rules:

```text
Worker assigned to House A can create daily entries only for House A.
Worker cannot see financial reports unless explicitly granted.
Sales user can sell eggs but cannot edit medication records.
Vet can add health notes but cannot alter sales or expenses.
Owner can override locked records, with audit log.
```

## 4.3 Permission matrix

| Module | Owner | Manager | Worker | Sales | Vet | Read-only |
|---|---:|---:|---:|---:|---:|---:|
| Dashboard | full | full | limited | sales view | health view | view |
| Farm setup | full | edit | no | no | view | view |
| Flocks | full | edit | view assigned | view | health view | view |
| Daily entry | full | edit | create assigned | no | view | view |
| Egg inventory | full | edit | view assigned | view/sell | view | view |
| Sales | full | view | no | create/edit | no | view |
| Expenses | full | edit | no | no | no | view if allowed |
| Medication | full | edit | no | view withdrawal only | create/edit | view |
| Reports | full | full | limited | sales reports | health reports | view |
| Users/Roles | full | limited | no | no | no | no |
| Audit log | full | view | no | no | no | no |

---

# 5. Phase Plan

## 5.1 Phase 1 — Walking Skeleton

Build the smallest real system that can run an egg farm’s core loop.

### Phase 1 modules

1. Auth + basic roles
2. Account / farm / house / flock setup
3. Daily entry: eggs, feed, water, mortality
4. Daily entry states and locking
5. Egg lots + egg inventory ledger
6. Sales + customer payments + lot allocation
7. Feed inventory + feed usage
8. Basic medication withdrawal flag
9. Basic expenses
10. Dashboard
11. Core reports
12. Audit log for critical edits
13. CSV export / backup

### Phase 1 acceptance criteria

The farm can:

- Create a flock and assign it to a house.
- Submit a daily entry in under 60 seconds.
- Generate egg lots from submitted production.
- Sell eggs and reduce inventory.
- Allocate sale quantities from egg lots using FIFO by default.
- Block or warn on restricted egg lots.
- Track feed usage and calculate feed cost from inventory lot where possible.
- Track mortality and calculate current live birds.
- View dashboard KPIs.
- Export records for backup.
- Audit critical changes.

## 5.2 Phase 1.5 — Operational Hardening

Add:

- Legacy import wizard
- Inventory reconciliation
- Alert center
- Email digest for critical alerts
- House-level RBAC UI
- Packaging inventory
- Additives/supplements
- Vaccination records
- Flock profitability allocation rules

## 5.3 Phase 2 — Farm Operations

Add:

- Beak treatment and follow-ups
- Water quality testing
- Environment/lighting tracking
- Litter/sanitation
- Equipment/maintenance
- Advanced reports
- Advanced alert thresholds
- Excel import/export

## 5.4 Phase 3 — Advanced

Add:

- Offline mobile mode
- IoT integrations
- Barcode/QR labels
- Multi-farm benchmarking
- SaaS billing/admin
- Accounting integrations
- Forecasting and breed baseline comparisons

---

# 6. Core Data Model

## 6.1 Accounts

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

## 6.2 Users and roles

```text
users
- id
- account_id
- name
- email
- password_hash
- default_farm_id
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

## 6.3 Farms

```text
farms
- id
- account_id
- name
- address
- phone
- email
- timezone
- currency
- unit_system: imperial / metric
- active
- created_at
- updated_at
```

## 6.4 Houses

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

## 6.5 Flocks

```text
flocks
- id
- account_id
- farm_id
- house_id
- name
- breed_or_strain
- source_supplier_id nullable
- placement_date
- starting_bird_count
- production_stage: brooding / growing / pre_lay / laying / molting / depleted
- status: active / inactive / depleted / sold
- expected_start_lay_date
- expected_depletion_date
- active_egg_withdrawal_until nullable
- beak_treated_status: unknown / no / yes / supplier_performed
- notes
- created_at
- updated_at
```

## 6.6 Flock movements

For future support of transfers between houses:

```text
flock_movements
- id
- account_id
- farm_id
- flock_id
- from_house_id nullable
- to_house_id nullable
- movement_date
- bird_count
- reason
- notes
- created_by
- created_at
```

Phase 1 rule:

```text
One active flock belongs to one house at a time.
Transfers use flock_movements and update flocks.house_id.
```

---

# 7. Daily Entry

## 7.1 Purpose

The daily entry screen is the make-or-break workflow.

Target:

```text
A worker should complete daily entry for one house/flock in under 60 seconds.
```

## 7.2 Daily entry states

```text
draft
submitted
locked
manager_adjusted
voided
```

### State rules

| State | Meaning | Who can edit |
|---|---|---|
| draft | Incomplete or copied from previous day | creator, manager |
| submitted | Official daily record | worker until cutoff, manager |
| locked | Closed after configured days | owner/admin only |
| manager_adjusted | Submitted record adjusted after review | manager/owner |
| voided | Entry cancelled but preserved for audit | owner/admin |

Default lock rule:

```text
Daily entries lock after 7 farm-local days.
```

## 7.3 Uniqueness rule

Database constraint:

```text
UNIQUE(account_id, farm_id, house_id, flock_id, date)
```

If a duplicate entry is attempted:

- Same user, draft exists → open existing draft.
- Different user, draft exists → show conflict warning.
- Submitted exists → reject unless manager override.
- Locked exists → owner override only.

## 7.4 Fields

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

Egg production, feed usage, water usage, mortality, and environment are linked to the daily entry.

## 7.5 Daily entry UX requirements

- Remember last selected farm/house/flock per user.
- Provide **Copy from yesterday**.
- Show Draft/Submitted/Locked status.
- Allow saving partial drafts.
- Warn if expected fields are blank.
- Distinguish zero from blank:
  - `0 deaths` means no deaths.
  - blank deaths means not entered.
- Submit action generates ledger side effects.

---

# 8. Egg Production, Egg Lots, and Egg Inventory

## 8.1 Egg grades

Egg grades are configurable per farm.

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

## 8.2 Production grade records

Instead of hardcoding egg categories into one table, use grade rows.

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

## 8.3 Egg lots

A submitted production entry creates one egg lot per saleable grade.

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

## 8.4 Egg inventory movements

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

### Movement rules

| Event | Required movement |
|---|---|
| Daily entry submitted | `production` movement per generated egg lot |
| Sale confirmed | `sale` movement per allocation |
| Eggs discarded | `discard` movement |
| Inventory count corrected | `reconciliation` movement |
| Entry voided | reversing `void` movements |
| Internal use | `internal_use` movement |

## 8.5 Cached stock

`egg_lots.quantity_available` is a cached balance.

Rule:

```text
quantity_available = quantity_produced + sum(egg_inventory_movements.quantity_delta)
```

It may be stored for fast reads, but it must be rebuildable from movements.

## 8.6 Traceability report

The system must be able to answer:

```text
Customer sale → sales item → allocations → egg lots → flock → house → production date → daily entry
```

---

# 9. Sales, Customers, Payments, and Allocation

## 9.1 Customers

```text
customers
- id
- account_id
- farm_id
- name
- phone
- email
- address
- customer_type: retail / wholesale / store / restaurant / family / other
- default_price_level
- active
- notes
- created_at
- updated_at
```

## 9.2 Sales orders

```text
sales_orders
- id
- account_id
- farm_id
- customer_id
- order_date
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

## 9.3 Sales order items

```text
sales_order_items
- id
- account_id
- sales_order_id
- egg_grade_id
- quantity_in_eggs
- display_unit: egg / dozen / tray / carton / case
- display_quantity
- unit_price_cents
- line_total_cents
- created_at
- updated_at
```

## 9.4 Sales item allocations

```text
sales_order_item_allocations
- id
- account_id
- sales_order_item_id
- egg_lot_id
- quantity_in_eggs
- allocation_method: fifo / manual
- egg_inventory_movement_id
- created_at
```

## 9.5 Sale confirmation transaction

When a sales order is confirmed:

1. Validate inventory availability.
2. Allocate from egg lots using FIFO by production date unless manual selection is used.
3. Check each candidate egg lot for restriction.
4. Block restricted lots unless owner override is enabled and allowed.
5. Create `sales_order_item_allocations`.
6. Create `egg_inventory_movements` with `movement_type = sale`.
7. Update cached egg lot balances.
8. Update sales totals and customer balance.
9. Write audit log.

## 9.6 Withdrawal sales rule

Default:

```text
Restricted egg lots cannot be sold.
```

Optional owner-only override:

- Requires reason.
- Creates audit log.
- Clearly marks sale as containing restricted eggs.

Recommended default for Phase 1:

```text
Hard block restricted egg lots.
No override until Phase 1.5.
```

## 9.7 Payments

```text
payments
- id
- account_id
- farm_id
- sales_order_id
- customer_id
- payment_date
- amount_cents
- method: cash / check / card / bank_transfer / mobile_payment / other
- reference_number
- notes
- created_by
- created_at
```

---

# 10. Feed Inventory and Feed Usage

## 10.1 Inventory items

All non-egg stock uses the general inventory system.

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

## 10.2 Inventory lots

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

## 10.3 General inventory movements

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
- farm_id
- house_id nullable
- flock_id nullable
- reference_type
- reference_id
- reason
- notes
- created_by
- created_at
```

## 10.4 Feed usage

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

## 10.5 Feed usage transaction

When feed usage is saved:

1. Create `feed_usage`.
2. Select inventory lot:
   - FIFO by received date if lot tracking is enabled.
   - Latest lot or average cost if simplified.
3. Calculate cost.
4. Create `inventory_movements` with `movement_type = usage`.
5. Update cached lot balance.
6. Update feed KPIs.

## 10.6 Feed KPIs

- Feed per bird per day
- Feed cost per day
- Feed cost per dozen
- Feed cost per tray
- Feed-to-egg ratio
- Days of feed remaining
- Feed inventory value

---

# 11. Water and Hydration

## 11.1 Water usage

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

## 11.2 Water KPIs

- Water per bird per day
- Water-to-feed ratio
- 7-day water trend
- Drop compared to rolling average

Formula:

```text
Water-to-feed ratio = water quantity / feed quantity
```

Units must be normalized before calculation.

---

# 12. Medication, Withdrawal, Vaccines, Additives

## 12.1 Medication applications

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

## 12.2 Active medication definition

A medication is active when:

```text
status = active
OR
start_date <= farm_today AND (end_date IS NULL OR end_date >= farm_today)
```

Withdrawal is active when:

```text
egg_withdrawal_until IS NOT NULL AND egg_withdrawal_until >= farm_today
```

## 12.3 Medication save transaction

1. Create medication application.
2. Create inventory movement usage if stock item is selected.
3. Update `flocks.active_egg_withdrawal_until` to max active withdrawal date.
4. Create alert if withdrawal is active.
5. Audit log.

## 12.4 Egg lot restriction rule

When daily egg lots are generated:

```text
if flock.active_egg_withdrawal_until >= production_date:
    egg_lot.status = restricted
    egg_lot.restricted_until = flock.active_egg_withdrawal_until
```

## 12.5 Vaccination records

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

Saving a vaccination record creates inventory usage movement if stock is selected.

## 12.6 Additive and supplement applications

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
- reason: heat_stress / shell_quality / stress / routine / recovery / other
- notes
- created_by
- created_at
```

Saving an additive application creates an inventory usage movement.

---

# 13. Mortality, Culling, and Live Birds

## 13.1 Mortality records

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

## 13.2 Current live birds

MVP decision:

```text
Current live birds are recomputed from starting count minus mortality/culls/transfers.
```

Backdated edits may alter historical KPI reports.

All backdated edits are audit logged.

Future option:

```text
Daily flock snapshots for immutable historical reporting.
```

## 13.3 Mortality alerts

Use both absolute and relative thresholds.

Example default:

```text
Alert if today mortality exceeds max(absolute_threshold, 3x 7-day average)
```

Thresholds are configurable by farm and optionally by flock/stage.

---

# 14. Health and Welfare

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

## 14.2 Welfare observation tags

```text
welfare_observation_tags
- id
- account_id
- health_event_id
- observation_type
```

Default observation types:

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

---

# 15. Beak Treatment

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

```text
beak_treatment_followups
- id
- account_id
- beak_treatment_id
- followup_date
- feed_intake_status
- water_intake_status
- mortality_count
- pecking_observed
- cannibalism_observed
- uniformity_rating
- corrective_action
- notes
- created_by
- created_at
```

---

# 16. Expenses and Profitability

## 16.1 Expenses

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
- allocation_method: direct / bird_count_share / revenue_share / manual
- payment_method
- receipt_attachment_id nullable
- notes
- created_by
- created_at
```

## 16.2 Expense categories

```text
expense_categories
- id
- account_id
- farm_id nullable
- name
- active
```

## 16.3 Profitability allocation rules

Direct expenses:

```text
Assign directly to flock/house/farm.
```

Shared expenses:

```text
Allocate by bird count, revenue share, or manual percentage.
```

Brooding costs:

```text
MVP: count as direct flock costs.
Future: amortize over expected laying period.
```

---

# 17. Alerts and Tasks

## 17.1 Alert thresholds

```text
alert_thresholds
- id
- account_id
- farm_id
- flock_id nullable
- threshold_type
- value
- unit
- enabled
- created_at
- updated_at
```

Thresholds may be global per farm or customized per flock.

## 17.2 Alerts

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

Critical alerts should appear in:

- Dashboard badge
- Alert center
- Email digest, Phase 1.5
- Push notification, Phase 3

## 17.3 Alert types

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

## 17.4 Tasks

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

---

# 18. Inventory Reconciliation

## 18.1 Purpose

Real-world inventory drifts.

Examples:

- Eggs break after collection.
- Feed bag weight is inaccurate.
- Worker forgets to record a usage.
- Packaging count is wrong.

## 18.2 Reconciliation workflow

1. User selects item/grade/location.
2. System shows expected quantity.
3. User enters physical count.
4. System calculates difference.
5. User must provide reason.
6. System creates reconciliation movement.
7. Audit log records change.

## 18.3 Reconciliation movement

```text
movement_type = reconciliation
quantity_delta = physical_count - system_count
reason required
```

---

# 19. Legacy Import

## 19.1 Purpose

Most farms will already have data in notebooks, Excel, WhatsApp, or memory.

## 19.2 Legacy import wizard

Steps:

1. Select import type.
2. Upload CSV/Excel.
3. Map columns.
4. Set farm/house/flock context.
5. Preview validation.
6. Import as backdated records.
7. Create audit import batch.

## 19.3 Import types

- Existing flocks with initial bird counts
- Historical egg production
- Historical mortality
- Customers
- Sales history
- Feed purchases
- Expenses
- Starting inventory counts

## 19.4 Backdated flag

Imported historical records should include:

```text
import_batch_id
is_backdated = true
source = legacy_import
```

---

# 20. Backup and Recovery

## 20.1 Backups

Phase 1:

- Manual CSV export
- Full account export
- Database dump instructions for self-hosted deployments

Phase 1.5:

- Scheduled automated backups
- Configurable storage target
- Backup health check

## 20.2 Recovery

The system must define:

- Restore from backup
- Rollback failed imports
- Export before destructive changes
- Audit log preservation

---

# 21. Audit Log

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
- Medication withdrawal overrides
- Inventory reconciliation
- Sales voids
- Expense changes
- User/role changes
- Import/export

---

# 22. Dashboard

## 22.1 Main dashboard cards

- Eggs collected today
- Saleable eggs today
- Hen-day production %
- Active egg withdrawal warnings
- Restricted egg lots
- Current live birds
- Feed used today
- Water used today
- Mortality today
- Egg inventory by grade
- Aging egg lots
- Sales today
- Expenses this week
- Active alerts
- Tasks due today

## 22.2 Flock dashboard

- Age
- Current live birds
- Production stage
- 7-day egg average
- Hen-day production %
- Mortality %
- Feed per bird
- Water per bird
- Water-to-feed ratio
- Feed cost per dozen
- Egg loss %
- Active withdrawal status
- Recent health events

---

# 23. Reports

## 23.1 Core Phase 1 reports

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

## 23.2 Traceability report

Must support:

```text
Sale → customer → sales items → allocations → egg lots → flock → daily entry
```

## 23.3 KPI formulas

```text
Current live birds =
starting birds - deaths - culls +/- transfers
```

```text
Hen-day production % =
total eggs collected / current live birds × 100
```

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

# 24. Use Cases

## UC-001 Create account

**Actor:** Owner  
**Goal:** Create the account root for all farm data.

**Flow:**

1. Owner signs up.
2. System creates account.
3. Owner creates first farm.
4. Owner becomes account owner.

**Acceptance criteria:**

- Account exists before farm creation.
- Farm has `account_id`.
- Owner has full permissions.

---

## UC-010 Create farm, house, and flock

**Actor:** Owner/Manager  
**Goal:** Set up the production structure.

**Flow:**

1. Create farm.
2. Add one or more houses.
3. Create flock.
4. Assign flock to house.
5. Enter starting bird count and placement date.

**Acceptance criteria:**

- Flock belongs to account, farm, and house.
- Flock age auto-calculates from placement date.
- Starting bird count is immutable after submitted records exist unless owner override.

---

## UC-020 Daily entry draft

**Actor:** Worker  
**Goal:** Start daily entry without final submission.

**Flow:**

1. Worker opens Daily Entry.
2. System defaults to last selected farm/house/flock.
3. Worker clicks Copy from Yesterday or enters values manually.
4. Worker saves draft.

**Acceptance criteria:**

- Draft does not create inventory movements.
- Draft can be edited by creator/manager.
- Duplicate draft for same farm/house/flock/date is prevented.

---

## UC-021 Submit daily entry

**Actor:** Worker/Manager  
**Goal:** Submit official daily record.

**Flow:**

1. Worker reviews draft.
2. System validates required fields.
3. Worker submits.
4. System creates production grade records.
5. System creates egg lots.
6. System creates production inventory movements.
7. System creates feed usage movement if feed entered.
8. System creates water usage record if water entered.
9. System creates mortality record if mortality entered.
10. System recalculates KPIs and alerts.

**Acceptance criteria:**

- Submitted entry creates ledger side effects exactly once.
- Re-submission does not duplicate inventory movements.
- Saleable egg grades create egg lots.
- Restricted status is applied if flock is under withdrawal.
- Audit log records submission.

---

## UC-022 Edit submitted daily entry

**Actor:** Manager  
**Goal:** Correct an error.

**Flow:**

1. Manager opens submitted entry.
2. Manager edits values.
3. System requires reason.
4. System creates reversing/adjusting movements.
5. System updates lots and balances.
6. System records audit log.

**Acceptance criteria:**

- Original history is preserved.
- Adjustments are traceable.
- Locked entries require owner/admin override.

---

## UC-030 Sell eggs

**Actor:** Sales user/Manager  
**Goal:** Sell eggs and reduce inventory.

**Flow:**

1. Sales user creates order.
2. Adds egg grade and quantity.
3. System suggests FIFO allocations from available egg lots.
4. User reviews lots, ages, and restrictions.
5. System blocks restricted lots.
6. User confirms order.
7. System creates allocations and sale movements.
8. Customer balance updates.
9. Receipt/invoice is generated.

**Acceptance criteria:**

- Inventory decreases only through movements.
- Sale line can allocate from multiple lots.
- Traceability report can trace sale to flock.
- Restricted lots are blocked.

---

## UC-031 Manual egg lot allocation

**Actor:** Manager/Sales  
**Goal:** Choose specific egg lots for a sale.

**Flow:**

1. User opens allocation panel.
2. Selects lots by grade/date/flock/location.
3. System validates quantity.
4. System blocks restricted lots.
5. User confirms allocation.

**Acceptance criteria:**

- Allocation cannot exceed available lot quantity.
- Restricted lots are visibly marked.
- Manual allocation is stored as `allocation_method = manual`.

---

## UC-040 Record medication with egg withdrawal

**Actor:** Manager/Vet  
**Goal:** Track treatment and prevent unsafe sales.

**Flow:**

1. User selects flock.
2. Enters medication, dates, dose, reason.
3. Enters egg withdrawal date.
4. System creates medication record.
5. System creates inventory usage movement if item/lot selected.
6. System updates flock active withdrawal cache.
7. System creates alert.
8. Future egg lots produced during withdrawal are marked restricted.

**Acceptance criteria:**

- Withdrawal appears on flock detail and sales screen.
- Restricted lots cannot be allocated to sales.
- Audit log records medication creation/edit.

---

## UC-050 Record feed usage

**Actor:** Worker/Manager  
**Goal:** Track feed cost and consumption.

**Flow:**

1. Feed is entered in daily entry or feed usage screen.
2. System selects inventory lot by FIFO/default method.
3. System calculates cost.
4. System creates inventory movement.
5. Feed KPIs update.

**Acceptance criteria:**

- Feed usage reduces stock.
- Cost method is stored.
- Feed cost per dozen is calculable.

---

## UC-060 Reconcile inventory

**Actor:** Manager  
**Goal:** Correct inventory drift.

**Flow:**

1. Manager opens Reconciliation.
2. Selects egg lot or inventory item.
3. System shows expected balance.
4. Manager enters physical count.
5. Manager provides reason.
6. System creates reconciliation movement.
7. Audit log records correction.

**Acceptance criteria:**

- Stock cannot be changed without movement.
- Reason is required.
- Difference is visible in movement history.

---

## UC-070 Legacy import

**Actor:** Owner/Manager  
**Goal:** Bring existing farm records into system.

**Flow:**

1. User opens Legacy Import.
2. Chooses import type.
3. Uploads CSV/Excel.
4. Maps columns.
5. Previews validation results.
6. Imports records as backdated.
7. System creates import batch record.

**Acceptance criteria:**

- User can import existing flock with initial bird count.
- Historical production can be imported.
- Records are marked as legacy/backdated.
- Failed rows are reported.

---

## UC-080 Review alerts

**Actor:** Manager/Owner  
**Goal:** See and resolve farm issues.

**Flow:**

1. Alert badge appears in header.
2. User opens Alert Center.
3. User filters by severity/module.
4. User resolves/dismisses alert.
5. System records status.

**Acceptance criteria:**

- Critical alerts are visible without opening reports.
- Resolved alerts remain in history.
- Email digest can be enabled in Phase 1.5.

---

# 25. Screen List and Wireframe Coverage

## 25.1 Updated v2 wireframes

The v2 wireframe package includes:

1. Login
2. Account/Farm Setup
3. Dashboard
4. Daily Entry
5. Flocks
6. Flock Detail
7. Egg Production
8. Egg Lots / Traceability
9. Egg Inventory
10. Sales Order
11. Sales Lot Allocation
12. Customers
13. Feed & Inventory
14. Inventory Reconciliation
15. Water & Additives
16. Health & Welfare
17. Medication & Withdrawal
18. Beak Treatment
19. Expenses
20. Alerts & Tasks
21. Reports
22. Users & Roles
23. Audit Log
24. Legacy Import
25. Settings
26. Mobile Daily Entry

## 25.2 Wireframe disclaimer

Wireframes are low-fidelity. They define layout intent, major controls, and data relationships. They are not final visual design.

---

# 26. Open Questions Resolved in v2

| Question | v2 Decision |
|---|---|
| Multi-farm day one? | Add account root now. UI can remain simple. |
| Should egg inventory track age by production date? | Yes. Egg lots include production date. |
| Should sales require source flock selection? | Sales allocate from egg lots; egg lots carry flock source. |
| Should withdrawal block sales completely? | Phase 1 hard block restricted lots. |
| Can workers edit own entries? | Draft yes. Submitted only until cutoff if allowed. Locked no. |
| Should daily entries be locked? | Yes, default 7 days. |
| Stock from movements only or cached? | Ledger source of truth + rebuildable cached balances. |
| Fiscal or calendar periods? | Calendar for MVP. |
| Web-only or self-hosted? | Web-first, account-ready. Self-hosted compatible. |
| Offline entry first release? | No. |
| Multiple houses per flock? | One active house at a time; transfers tracked. |
| Egg grades configurable? | Yes, per farm. |
| Packaging separate? | General inventory category in Phase 1; richer packaging later. |
| Dashboard scope? | Global farm selector. |
| Expense categories configurable? | Yes. |
| Data visibility? | Scoped by role and farm/house/flock. |
| Reports trend lines? | Core reports include 7-day/30-day trends. |

---

# 27. Build Order Recommendation

## Sprint 1 — Foundation

- Account/user/auth
- Farm/house/flock CRUD
- Role scope
- Audit log foundation

## Sprint 2 — Daily entry

- Daily entry states
- Duplicate prevention
- Copy from yesterday
- Egg grade entries
- Mortality
- Feed/water simple fields

## Sprint 3 — Egg lots and inventory

- Egg lot generation
- Egg inventory movement ledger
- Cached balances
- Egg inventory screens

## Sprint 4 — Sales

- Customers
- Sales orders
- FIFO allocation
- Sale movements
- Payment records

## Sprint 5 — Health safety

- Basic medication records
- Withdrawal cache
- Restricted egg lots
- Sale blocking

## Sprint 6 — Reports and hardening

- Dashboard
- Core reports
- Export/backup
- Audit screens
- Basic alerts

---

# 28. Developer Notes

## 28.1 Transaction boundaries

The following must be atomic database transactions:

- Submit daily entry
- Edit submitted daily entry
- Confirm sale
- Void sale
- Record medication with withdrawal
- Save feed usage
- Reconcile inventory
- Import batch

## 28.2 Idempotency

Submitting the same draft twice must not duplicate lots or movements.

Use:

```text
reference_type + reference_id + movement_type
```

and unique constraints where needed.

## 28.3 Money

Store money as integer cents.

```text
amount_cents integer
```

## 28.4 Quantities

Egg quantities should store base unit as individual eggs.

```text
quantity_in_eggs integer
```

Display units are presentation only.

## 28.5 Units

Normalize feed/water internally where possible.

- Feed: base unit grams or pounds, depending on system setting.
- Water: base unit liters or gallons, depending on system setting.

## 28.6 Auditability

Never delete business records physically in normal operation.

Use status:

```text
voided
cancelled
inactive
```

---

# 29. Summary

The v2 system is now centered around a safe operational loop:

```text
Daily Entry
→ Egg Lots
→ Inventory Movements
→ Sales Allocations
→ Traceability
→ Reports
```

And the safety loop:

```text
Medication
→ Flock Withdrawal Cache
→ Restricted Egg Lots
→ Sale Blocking
→ Audit Log
```

This gives the system enough structure to support real egg-farm operations without building the entire enterprise platform in the first release.
