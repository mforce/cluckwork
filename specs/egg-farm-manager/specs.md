# Poultry Egg Farm Management System — Product Specification

## 1. Overview

### 1.1 Product Name

Working name: **Egg Farm Manager**

### 1.2 Purpose

Egg Farm Manager is a web/mobile-friendly farm management system for poultry egg-producing farms. It helps farm owners and staff manage flocks, houses, daily egg production, feed, water, supplements, medication, vaccinations, health events, beak treatment, egg inventory, sales, expenses, alerts, and reports.

The system is designed for small-to-medium egg farms first, with a path toward multi-farm commercial operations later.

### 1.3 Primary Goals

- Make daily farm data entry fast and reliable.
- Track egg production by house, flock, grade, and date.
- Track feed, water, supplements, medications, vaccines, and packaging inventory.
- Monitor flock health, mortality, culling, and welfare procedures.
- Manage egg inventory, sales, customers, payments, and expenses.
- Calculate production and financial KPIs.
- Provide alerts for production drops, mortality spikes, low stock, withdrawal periods, and due tasks.
- Export data for accounting, analysis, and backup.

### 1.4 Non-Goals for MVP

The first version should not include:

- Hatchery management.
- Broiler meat production.
- Processing/slaughter operations.
- Feed mill formula management.
- Contract grower settlement.
- Complex accounting ledger.
- Payroll.
- IoT sensor integrations.
- Advanced AI prediction.

These can be added in future phases.

---

## 2. Target Users and Roles

### 2.1 User Personas

#### Farm Owner

The owner needs to see performance, profit, inventory, and alerts. They care about production trends, feed cost, sales, expenses, customer balances, and overall profitability.

#### Farm Manager

The manager is responsible for daily operations. They configure flocks, assign houses, check daily records, manage inventory, review health issues, and resolve alerts.

#### Farm Worker

The worker enters daily records: eggs collected, egg grades, feed used, water used, deaths, culls, notes, and simple task completion.

#### Sales/Admin User

The sales/admin user handles customers, orders, egg sales, payments, invoices, delivery notes, and customer balances.

#### Vet/Consultant

The vet or consultant views flock health history, medication, vaccination, mortality, symptoms, and welfare observations. They may add health recommendations.

#### Read-Only User

A read-only user can view dashboards and reports but cannot edit farm data.

### 2.2 Roles and Permissions

| Role | Main Permissions |
|---|---|
| Owner/Admin | Full system access, settings, users, reports, delete/restore records |
| Manager | Farm operations, flocks, inventory, sales, expenses, reports, alerts |
| Worker | Daily entry, mortality, notes, task completion, view assigned houses |
| Sales | Customers, sales, invoices, payments, egg inventory view |
| Vet/Consultant | Health, vaccination, medication, welfare records, read production data |
| Read-Only | View dashboards and reports only |

### 2.3 Permission Rules

- Workers should not be able to edit financial reports or delete records.
- Sales users should not be able to change flock counts or medication records.
- Medication and vaccination edits should be restricted to managers, admins, and vet users.
- Inventory adjustments should require manager or admin permission.
- All record changes should be logged in the audit log.

---

## 3. System Scope

### 3.1 MVP Modules

1. Authentication and users
2. Farm, house, and flock setup
3. Daily entry
4. Egg production
5. Egg grading and inventory
6. Feed inventory and feed usage
7. Water usage and hydration tracking
8. Supplements and additives
9. Medication and vaccines
10. Mortality and culling
11. Health and welfare events
12. Beak treatment
13. Bedding, litter, cleaning, and sanitation
14. Environment and lighting
15. Equipment and maintenance
16. Customers
17. Sales orders and payments
18. Expenses
19. Tasks, reminders, and alerts
20. Dashboard
21. Reports
22. Import/export
23. Settings
24. Audit log

### 3.2 Future Modules

- Offline mobile mode
- Barcode/QR labels
- Accounting integration
- IoT integrations for water/feed/environment sensors
- Forecasting and benchmarking
- Multi-farm enterprise hierarchy
- Delivery route tracking
- API access
- Mobile app push notifications

---

## 4. Core Domain Model

### 4.1 Entity Hierarchy

```text
Company / Account
  Farm
    House / Coop
      Flock / Batch
        Daily Records
        Egg Production
        Feed Usage
        Water Usage
        Mortality
        Health Events
        Treatments
        Vaccinations
        Beak Treatment
        Environment Records
```

### 4.2 Central Concept: Flock

The flock is the center of the system. Most records should link to a flock whenever possible.

Examples:

- Egg production is linked to a flock.
- Feed usage is linked to a flock.
- Water usage is linked to a flock.
- Mortality is linked to a flock.
- Medication is linked to a flock.
- Beak treatment is linked to a flock.
- Profitability can be calculated by flock.

### 4.3 Important Calculated Values

```text
Current live birds = starting birds - deaths - culls - birds sold/transferred

Hen-day production % = total eggs collected / current live birds × 100

Saleable egg % = good eggs / total eggs collected × 100

Egg loss % = non-saleable eggs / total eggs collected × 100

Dozens produced = good eggs / 12

Trays produced = good eggs / 30

Feed cost per dozen = feed cost / dozens produced

Water per bird = water consumed / current live birds

Cumulative mortality % = cumulative deaths / starting birds × 100

Profit = egg revenue - expenses

Profit per dozen = profit / dozens sold
```

---

## 5. Global UX Principles

### 5.1 Daily Entry Must Be Fast

A worker should be able to enter one house's daily records in under 60 seconds.

Design priorities:

- Large input fields.
- Minimal required fields.
- Save partial daily records.
- Default to today's date.
- Remember last selected farm/house/flock.
- Allow quick duplication from previous day.
- Use mobile-friendly layouts.

### 5.2 Records Should Be Structured, Not Just Notes

Important events should have dedicated forms and fields:

- Mortality
- Medication
- Vaccination
- Beak treatment
- Egg production
- Feed usage
- Water usage
- Sales
- Expenses

Notes should supplement structured data, not replace it.

### 5.3 Every Important Record Should Have Context

Most operational records should include:

- Date
- Farm
- House
- Flock, if applicable
- User who created it
- Created timestamp
- Updated timestamp
- Notes

### 5.4 Prevent Data Errors

The system should:

- Warn if egg category totals do not match total eggs.
- Warn if mortality exceeds current live birds.
- Warn if inventory usage exceeds available stock.
- Warn if eggs are sold during medication withdrawal.
- Warn if duplicate daily entries exist for the same flock and date.
- Warn if feed/water usage is unusually high or low.

---

## 6. Detailed Use Cases

## 6.1 Authentication and User Management

### UC-001: Log In

**Actor:** Any user  
**Goal:** Access the system securely.

**Preconditions:**

- User account exists.

**Main Flow:**

1. User opens login page.
2. User enters email and password.
3. System validates credentials.
4. System loads the user's default dashboard.

**Alternative Flows:**

- Invalid credentials: show error.
- Disabled account: deny access.
- Password reset needed: direct user to reset flow.

**Acceptance Criteria:**

- User can log in with valid credentials.
- User cannot access unauthorized modules.
- User actions are associated with their user ID.

### UC-002: Create User

**Actor:** Admin  
**Goal:** Add a staff member.

**Main Flow:**

1. Admin opens Users.
2. Admin clicks Add User.
3. Admin enters name, email, role, assigned farm/house access.
4. System sends invite or creates account.
5. New user appears in Users list.

**Acceptance Criteria:**

- Admin can assign a role.
- Admin can disable user later.
- User creation is logged.

### UC-003: Assign Role

**Actor:** Admin  
**Goal:** Control access.

**Main Flow:**

1. Admin opens a user profile.
2. Admin selects role.
3. Admin saves.
4. System applies permissions immediately.

**Acceptance Criteria:**

- Role change takes effect on next page load or immediately.
- Audit log records old and new role.

---

## 6.2 Farm Setup

### UC-010: Create Farm

**Actor:** Owner/Admin  
**Goal:** Create a farm profile.

**Main Flow:**

1. User opens Farms.
2. User clicks Add Farm.
3. User enters farm name, address, currency, timezone, unit system.
4. User saves.
5. Farm appears in farm selector.

**Acceptance Criteria:**

- Farm can be selected on records.
- Farm can be deactivated but not hard-deleted if records exist.

### UC-011: Create House/Coop

**Actor:** Manager/Admin  
**Goal:** Set up houses for flocks.

**Main Flow:**

1. User opens Houses.
2. User clicks Add House.
3. User enters name, type, capacity, location, notes.
4. User saves.

**Acceptance Criteria:**

- House belongs to a farm.
- House capacity can be compared against flock size.
- House can be marked inactive.

---

## 6.3 Flock Management

### UC-020: Create Flock

**Actor:** Manager/Admin  
**Goal:** Start tracking a flock.

**Main Flow:**

1. User opens Flocks.
2. User clicks Add Flock.
3. User enters name, house, breed/strain, supplier, placement date, starting bird count, production stage, expected start lay date.
4. System calculates age.
5. User saves.

**Acceptance Criteria:**

- Flock appears on dashboard and daily entry.
- Flock age updates automatically.
- Current live birds starts equal to starting bird count.

### UC-021: Move Flock to Another House

**Actor:** Manager/Admin  
**Goal:** Record a house transfer.

**Main Flow:**

1. User opens flock profile.
2. User selects Move Flock.
3. User chooses new house and effective date.
4. User enters notes.
5. System records movement history.

**Acceptance Criteria:**

- Future records default to new house.
- Historical records remain tied to original house.
- Movement is visible in flock timeline.

### UC-022: Deplete Flock

**Actor:** Manager/Admin  
**Goal:** Close a flock when production ends.

**Main Flow:**

1. User opens flock profile.
2. User clicks Deplete Flock.
3. User enters depletion date, reason, final live bird count, sale/disposal notes.
4. System marks flock as depleted.

**Acceptance Criteria:**

- Depleted flock no longer appears in daily entry by default.
- Reports can still include historical data.
- Flock cannot receive new daily entries unless explicitly reopened.

---

## 6.4 Daily Entry

### UC-030: Enter Daily Records for a House

**Actor:** Worker/Manager  
**Goal:** Record daily farm activity quickly.

**Main Flow:**

1. User opens Daily Entry.
2. System defaults date to today.
3. User selects farm, house, and flock.
4. User enters eggs collected and grade breakdown.
5. User enters feed used.
6. User enters water used.
7. User enters deaths/culls if any.
8. User enters temperature, humidity, light hours, notes if available.
9. User saves.
10. System updates egg inventory, feed stock, water trend, mortality count, and dashboard KPIs.

**Alternative Flows:**

- User saves only egg data first, then returns later for feed/water.
- User enters no deaths; system records zero.
- User creates health event from daily entry if symptoms are observed.

**Acceptance Criteria:**

- User can complete required daily entry quickly.
- System prevents duplicate entries unless user confirms update.
- System updates related modules automatically.

### UC-031: Edit Daily Entry

**Actor:** Manager/Admin, or original worker if allowed  
**Goal:** Correct an error.

**Main Flow:**

1. User opens daily entry history.
2. User selects a record.
3. User edits values.
4. System recalculates related inventory and KPIs.
5. System records audit log.

**Acceptance Criteria:**

- Edits update egg inventory accurately.
- Old and new values are stored in audit log.
- Users without permission cannot edit locked records.

---

## 6.5 Egg Production and Inventory

### UC-040: Record Egg Production

**Actor:** Worker/Manager  
**Goal:** Track eggs produced by flock.

**Main Flow:**

1. User opens Egg Production or Daily Entry.
2. User selects date, flock, house.
3. User enters total eggs and grade/category counts.
4. User saves.
5. System creates egg inventory movements for saleable eggs.

**Acceptance Criteria:**

- Egg totals validate against category sum when required.
- Good/saleable eggs increase inventory.
- Non-saleable eggs are tracked separately.

### UC-041: Adjust Egg Inventory

**Actor:** Manager/Admin  
**Goal:** Correct stock count.

**Main Flow:**

1. User opens Egg Inventory.
2. User selects grade/location.
3. User clicks Adjust.
4. User enters adjustment quantity and reason.
5. System records movement.

**Acceptance Criteria:**

- Inventory adjustment requires reason.
- Adjustment is audit logged.
- Inventory count updates immediately.

### UC-042: Discard Eggs

**Actor:** Worker/Manager  
**Goal:** Remove bad eggs from inventory.

**Main Flow:**

1. User opens Egg Inventory.
2. User selects grade/lot/date group.
3. User clicks Discard.
4. User enters quantity and reason.
5. System creates discard movement.

**Acceptance Criteria:**

- Discarded eggs reduce available inventory.
- Discards appear in loss report.

---

## 6.6 Feed Inventory and Usage

### UC-050: Record Feed Purchase

**Actor:** Manager/Admin  
**Goal:** Add feed stock.

**Main Flow:**

1. User opens Inventory > Feed.
2. User clicks Add Purchase.
3. User selects supplier and feed item.
4. User enters quantity, unit, cost, lot number, expiry date, invoice number.
5. User saves.
6. System increases feed stock.

**Acceptance Criteria:**

- Feed stock increases by received quantity.
- Cost is stored for feed cost calculations.
- Lot/expiry tracking is optional but supported.

### UC-051: Record Feed Usage

**Actor:** Worker/Manager  
**Goal:** Track feed consumed by flock.

**Main Flow:**

1. User opens Daily Entry or Feed Usage.
2. User selects feed item and quantity used.
3. User links usage to house/flock.
4. User saves.
5. System reduces feed inventory.

**Acceptance Criteria:**

- Feed usage reduces stock.
- System calculates estimated cost.
- System warns if stock is insufficient.

---

## 6.7 Water and Hydration

### UC-060: Record Water Usage

**Actor:** Worker/Manager  
**Goal:** Track flock hydration.

**Main Flow:**

1. User opens Daily Entry or Water Usage.
2. User selects flock/house.
3. User enters water quantity or meter start/end.
4. User saves.
5. System calculates water per bird and water-to-feed ratio.

**Acceptance Criteria:**

- Water usage appears in flock trend.
- System can alert on abnormal drops or spikes.

### UC-061: Record Water Quality Test

**Actor:** Manager/Admin  
**Goal:** Track water safety and quality.

**Main Flow:**

1. User opens Water Quality.
2. User clicks Add Test.
3. User enters pH, chlorine, TDS, temperature, bacteria test result, treatment used.
4. User saves.

**Acceptance Criteria:**

- Test results can be viewed by date/source.
- Failed bacteria test can trigger alert.

---

## 6.8 Additives, Supplements, Medications, and Vaccines

### UC-070: Add Inventory Item

**Actor:** Manager/Admin  
**Goal:** Create supplement, medication, vaccine, packaging, or supply item.

**Main Flow:**

1. User opens Inventory.
2. User clicks Add Item.
3. User enters name, category, unit, supplier, default cost, lot/expiry requirements, withdrawal tracking setting.
4. User saves.

**Acceptance Criteria:**

- Item can be used in purchases and usage records.
- Medication/vaccine items can require lot and expiry.

### UC-071: Apply Supplement/Additive

**Actor:** Worker/Manager  
**Goal:** Track usage of vitamin C, electrolytes, calcium, probiotics, etc.

**Main Flow:**

1. User opens Additive Application.
2. User selects item, lot, flock/house, date range.
3. User enters dosage, delivery method, quantity used, reason.
4. User saves.
5. System reduces inventory.

**Acceptance Criteria:**

- Additive usage appears in flock timeline.
- Inventory decreases.
- User can correlate additive use with egg production and shell quality.

### UC-072: Apply Medication

**Actor:** Manager/Vet/Admin  
**Goal:** Record treatment and withdrawal period.

**Main Flow:**

1. User opens Medication.
2. User selects flock, medication item, lot, date range, dosage, delivery method, reason, diagnosis.
3. User enters authorized by and egg withdrawal date.
4. User saves.
5. System creates withdrawal alert if applicable.

**Acceptance Criteria:**

- Medication record appears in flock health timeline.
- Eggs from flock are flagged during withdrawal.
- Sales screen warns or blocks sale of restricted eggs.

### UC-073: Record Vaccination

**Actor:** Manager/Vet/Admin  
**Goal:** Track vaccination history.

**Main Flow:**

1. User opens Vaccinations.
2. User selects flock, vaccine, lot, date, disease target, method, birds vaccinated.
3. User enters next due date if applicable.
4. User saves.
5. System creates reminder for next due date.

**Acceptance Criteria:**

- Vaccination appears in flock timeline.
- Next due date appears in tasks/alerts.

---

## 6.9 Mortality and Culling

### UC-080: Record Mortality

**Actor:** Worker/Manager  
**Goal:** Track deaths and culls.

**Main Flow:**

1. User opens Daily Entry or Mortality.
2. User selects date, flock, house.
3. User enters death count, cull count, reason, notes.
4. User saves.
5. System updates current live bird count.

**Acceptance Criteria:**

- Current live birds updates automatically.
- Cumulative mortality updates.
- Mortality spike can trigger alert.

### UC-081: Review Mortality Trend

**Actor:** Manager/Owner/Vet  
**Goal:** Detect health issues.

**Main Flow:**

1. User opens Reports > Mortality.
2. User filters by flock/date.
3. System shows daily and cumulative mortality.
4. User reviews reasons and notes.

**Acceptance Criteria:**

- Report shows mortality by flock and reason.
- Abnormal periods are visible.

---

## 6.10 Health and Welfare

### UC-090: Record Health Event

**Actor:** Worker/Manager/Vet  
**Goal:** Track symptoms, diagnosis, or welfare issues.

**Main Flow:**

1. User opens Health Events.
2. User selects flock/house/date.
3. User selects event type and severity.
4. User enters symptoms, diagnosis, action taken, vet name, follow-up date.
5. User attaches images/documents if needed.
6. User saves.

**Acceptance Criteria:**

- Health event appears in flock timeline.
- Follow-up date creates task.
- Critical severity triggers alert.

### UC-091: Record Welfare Observation

**Actor:** Worker/Manager/Vet  
**Goal:** Track behavior or welfare concern.

**Main Flow:**

1. User opens Welfare Observation.
2. User selects flock/house/date.
3. User selects observation: feather pecking, cannibalism, lameness, stress, poor shell quality, dirty eggs, wet litter, etc.
4. User enters severity and action.
5. User saves.

**Acceptance Criteria:**

- Welfare issue appears in health/welfare report.
- Severe issue can trigger alert.

---

## 6.11 Beak Treatment

### UC-100: Record Beak Treatment

**Actor:** Manager/Admin/Vet  
**Goal:** Track beak trimming or beak treatment history.

**Main Flow:**

1. User opens Flock > Procedures > Beak Treatment.
2. User clicks Add Record.
3. User enters date, bird age, birds treated, method, trim type, upper/lower beak amount, operator, equipment, reason, immediate mortality, notes.
4. User saves.
5. System updates flock profile shortcut.

**Acceptance Criteria:**

- Flock profile shows beak treatment status.
- Record appears in welfare/procedure timeline.
- Follow-up can be scheduled.

### UC-101: Record Beak Treatment Follow-Up

**Actor:** Worker/Manager/Vet  
**Goal:** Monitor flock after treatment.

**Main Flow:**

1. User opens beak treatment record.
2. User clicks Add Follow-Up.
3. User enters follow-up date, feed intake status, water intake status, mortality count, pecking observed, cannibalism observed, uniformity rating, corrective action.
4. User saves.

**Acceptance Criteria:**

- Follow-up links to original treatment.
- Poor feed/water intake or high mortality can trigger alert.

---

## 6.12 Bedding, Litter, Cleaning, and Sanitation

### UC-110: Record Litter Action

**Actor:** Worker/Manager  
**Goal:** Track litter additions, replacement, or cleanout.

**Main Flow:**

1. User opens Litter Records.
2. User selects house/flock/date.
3. User enters material, quantity, condition, ammonia smell, action.
4. User saves.

**Acceptance Criteria:**

- Litter condition appears in house timeline.
- Wet litter can trigger alert.

### UC-111: Record Sanitation Task

**Actor:** Worker/Manager  
**Goal:** Track cleaning and sanitation.

**Main Flow:**

1. User opens Sanitation.
2. User selects task type.
3. User enters date, house, item used, quantity used, performed by, next due date.
4. User saves.

**Acceptance Criteria:**

- Task appears in sanitation history.
- Next due date creates reminder.
- Item used reduces inventory if linked.

---

## 6.13 Environment and Lighting

### UC-120: Record Environment Conditions

**Actor:** Worker/Manager  
**Goal:** Track temperature, humidity, light hours, and ventilation.

**Main Flow:**

1. User opens Daily Entry or Environment.
2. User selects house/flock/date.
3. User enters temperature high/low/average, humidity, ammonia ppm, light hours, power outage, ventilation status, notes.
4. User saves.

**Acceptance Criteria:**

- Environment data appears in flock trend.
- High heat or low light can trigger alert.
- Production drops can be compared with environment history.

---

## 6.14 Equipment and Maintenance

### UC-130: Add Equipment

**Actor:** Manager/Admin  
**Goal:** Track farm equipment.

**Main Flow:**

1. User opens Equipment.
2. User clicks Add Equipment.
3. User enters name, type, farm, house, purchase date, status, notes.
4. User saves.

**Acceptance Criteria:**

- Equipment appears in equipment list.
- Maintenance can be linked to equipment.

### UC-131: Record Maintenance

**Actor:** Worker/Manager  
**Goal:** Track repairs and service.

**Main Flow:**

1. User opens equipment record.
2. User clicks Add Maintenance.
3. User enters issue, action taken, parts used, cost, performed by, next service date.
4. User saves.

**Acceptance Criteria:**

- Maintenance history appears on equipment profile.
- Next service date creates reminder.
- Cost can appear in expenses.

---

## 6.15 Customers, Sales, and Payments

### UC-140: Add Customer

**Actor:** Sales/Manager/Admin  
**Goal:** Create customer profile.

**Main Flow:**

1. User opens Customers.
2. User clicks Add Customer.
3. User enters name, phone, email, address, type, default price level, notes.
4. User saves.

**Acceptance Criteria:**

- Customer can be selected on sales order.
- Customer balance is calculated from unpaid orders.

### UC-141: Create Egg Sale

**Actor:** Sales/Manager/Admin  
**Goal:** Sell eggs and reduce inventory.

**Main Flow:**

1. User opens Sales.
2. User clicks New Sale.
3. User selects customer.
4. User adds egg grade, quantity, unit, unit price.
5. System calculates total.
6. User records payment status and delivery method.
7. User saves.
8. System reduces egg inventory.

**Alternative Flow:**

- If eggs are from a flock under medication withdrawal, system warns or blocks sale.

**Acceptance Criteria:**

- Sale reduces egg inventory.
- Customer balance updates.
- Revenue appears in reports.

### UC-142: Record Payment

**Actor:** Sales/Manager/Admin  
**Goal:** Track customer payment.

**Main Flow:**

1. User opens sales order or customer profile.
2. User clicks Record Payment.
3. User enters amount, date, method, reference number.
4. User saves.

**Acceptance Criteria:**

- Balance due updates.
- Payment appears in payment history.

---

## 6.16 Expenses

### UC-150: Record Expense

**Actor:** Manager/Admin  
**Goal:** Track operating costs.

**Main Flow:**

1. User opens Expenses.
2. User clicks Add Expense.
3. User enters date, category, description, amount, supplier, farm/house/flock link, payment method, receipt.
4. User saves.

**Acceptance Criteria:**

- Expense appears in reports.
- Expense can be linked to flock profitability.
- Receipt can be attached.

---

## 6.17 Tasks, Reminders, and Alerts

### UC-160: Create Task

**Actor:** Manager/Admin  
**Goal:** Assign work.

**Main Flow:**

1. User opens Tasks.
2. User clicks Add Task.
3. User enters title, description, due date, assigned user, priority, recurrence.
4. User saves.

**Acceptance Criteria:**

- Task appears on assigned user's dashboard.
- Overdue task appears in alerts.

### UC-161: System Generates Alert

**Actor:** System  
**Goal:** Warn user about important condition.

**Trigger Examples:**

- Feed stock low.
- Packaging stock low.
- Medication withdrawal active.
- Egg production dropped more than threshold.
- Mortality spiked.
- Water intake dropped.
- Vaccine due.
- Beak treatment follow-up due.

**Acceptance Criteria:**

- Alert appears on dashboard.
- User can mark alert resolved/dismissed.
- Alert links to related record.

---

## 6.18 Reports

### UC-170: View Production Report

**Actor:** Owner/Manager  
**Goal:** Analyze egg production.

**Main Flow:**

1. User opens Reports > Production.
2. User selects date range, farm, house, flock.
3. System shows total eggs, saleable eggs, grade breakdown, hen-day %, egg loss %, trend chart.
4. User exports report if needed.

**Acceptance Criteria:**

- Report can filter by date/farm/house/flock.
- Report can export CSV/Excel.

### UC-171: View Profitability Report

**Actor:** Owner/Manager  
**Goal:** Understand financial performance.

**Main Flow:**

1. User opens Reports > Profitability.
2. User selects period and flock/farm.
3. System shows revenue, expenses, feed cost, medication cost, packaging cost, profit, profit per dozen.
4. User exports report.

**Acceptance Criteria:**

- Report includes revenue and expenses.
- Report can calculate flock-level profitability when records are linked.

---

## 7. Data Model

## 7.1 Users and Roles

### users

```text
id
name
email
password_hash
default_farm_id
status: active / disabled
last_login_at
created_at
updated_at
```

### roles

```text
id
name
description
created_at
updated_at
```

### user_roles

```text
id
user_id
role_id
farm_id optional
created_at
```

---

## 7.2 Farm Setup

### farms

```text
id
name
owner_name
address
phone
email
currency
timezone
unit_system: imperial / metric
active
notes
created_at
updated_at
```

### houses

```text
id
farm_id
name
house_type: cage / deep_litter / free_range / aviary / other
capacity
location_description
active
notes
created_at
updated_at
```

### flocks

```text
id
farm_id
house_id
name
breed_or_strain
source_supplier_id optional
placement_date
starting_bird_count
production_stage: brooding / growing / pre_lay / laying / molting / depleted
bird_type: pullet / layer / breeder
status: active / inactive / depleted / sold
expected_start_lay_date
expected_depletion_date
notes
created_at
updated_at
```

### flock_movements

```text
id
flock_id
from_house_id
to_house_id
movement_date
bird_count
reason
notes
created_by
created_at
```

---

## 7.3 Egg Production and Inventory

### daily_egg_productions

```text
id
date
farm_id
house_id
flock_id
total_eggs_collected
good_eggs
cracked_eggs
dirty_eggs
soft_shell_eggs
small_eggs
medium_eggs
large_eggs
jumbo_eggs
double_yolk_eggs
discarded_eggs
internal_use_eggs
notes
created_by
created_at
updated_at
```

### egg_grades

```text
id
farm_id
name
sort_order
is_saleable
active
created_at
updated_at
```

### egg_inventory_movements

```text
id
date
farm_id
house_id optional
flock_id optional
grade_id
movement_type: production / sale / adjustment / discard / internal_use / transfer
quantity
unit: egg / dozen / tray / carton / case
quantity_in_eggs
location
reference_type
reference_id
notes
created_by
created_at
```

---

## 7.4 Inventory

### suppliers

```text
id
name
supplier_type: feed / medicine / vaccine / packaging / equipment / chicks / other
phone
email
address
notes
active
created_at
updated_at
```

### inventory_items

```text
id
farm_id optional
name
category: feed / water_additive / supplement / vitamin / electrolyte / probiotic / calcium_mineral / medication / vaccine / sanitizer / bedding_litter / packaging / pest_control / equipment_part / general_supply
unit
supplier_id optional
default_cost
requires_lot_tracking
requires_expiry_tracking
requires_withdrawal_tracking
storage_notes
active
notes
created_at
updated_at
```

### inventory_lots

```text
id
inventory_item_id
lot_number
expiry_date
received_date
quantity_on_hand
unit_cost
supplier_id optional
storage_location
notes
created_at
updated_at
```

### inventory_movements

```text
id
date
item_id
lot_id optional
movement_type: purchase / usage / adjustment / transfer / discard
quantity
unit
quantity_in_base_unit
farm_id optional
house_id optional
flock_id optional
reason
reference_type
reference_id
notes
created_by
created_at
```

### feed_usage

```text
id
date
farm_id
house_id
flock_id
item_id
lot_id optional
quantity_used
unit
quantity_in_base_unit
estimated_cost
notes
created_by
created_at
updated_at
```

---

## 7.5 Water

### water_usage

```text
id
date
farm_id
house_id
flock_id
water_quantity
unit: gallons / liters
water_source: well / municipal / tank / other
meter_start
meter_end
notes
created_by
created_at
updated_at
```

### water_quality_records

```text
id
date
farm_id
house_id optional
source
ph
chlorine_ppm
tds_ppm
temperature
bacteria_test_result: pass / fail / not_tested
treatment_used
filter_changed
notes
created_by
created_at
```

---

## 7.6 Treatments and Health

### additive_applications

```text
id
farm_id
house_id
flock_id
item_id
lot_id optional
delivery_method: water / feed / direct / spray / other
dosage_amount
dosage_unit
dosage_basis: per_gallon / per_liter / per_bird / per_feed_weight / fixed
quantity_used
reason: heat_stress / shell_quality / stress / routine / recovery / other
start_date
end_date
notes
created_by
created_at
updated_at
```

### medication_applications

```text
id
farm_id
house_id
flock_id
item_id
lot_id optional
start_date
end_date
dosage_amount
dosage_unit
dosage_basis
delivery_method
reason
diagnosis
authorized_by
egg_withdrawal_until
meat_withdrawal_until
notes
created_by
created_at
updated_at
```

### vaccination_records

```text
id
farm_id
house_id
flock_id
vaccine_item_id
lot_id optional
date_administered
bird_age_days
disease_target
method: drinking_water / spray / eye_drop / injection / wing_web / other
birds_vaccinated
administered_by
next_due_date
notes
created_by
created_at
updated_at
```

### mortality_records

```text
id
date
farm_id
house_id
flock_id
death_count
cull_count
reason: disease / injury / predator / heat_stress / weak / unknown / other
description
disposal_method
notes
created_by
created_at
updated_at
```

### health_events

```text
id
date
farm_id
house_id
flock_id
event_type: symptom / diagnosis / vet_visit / lab_test / injury / welfare_issue / other
severity: low / medium / high / critical
symptoms
diagnosis
action_taken
vet_name
follow_up_date
notes
created_by
created_at
updated_at
```

---

## 7.7 Beak Treatment

### beak_treatment_records

```text
id
farm_id
house_id
flock_id
date_performed
bird_age_days
birds_treated
method: infrared / hot_blade / other
trim_type: first_trim / second_trim / corrective / supplier_performed
upper_beak_amount
lower_beak_amount
operator_name
equipment_used
reason
immediate_mortality
notes
created_by
created_at
updated_at
```

### beak_treatment_followups

```text
id
beak_treatment_id
followup_date
feed_intake_status: normal / reduced / unknown
water_intake_status: normal / reduced / unknown
mortality_count
pecking_observed
cannibalism_observed
uniformity_rating: good / fair / poor
corrective_action
notes
created_by
created_at
updated_at
```

---

## 7.8 Environment, Litter, Sanitation, Equipment

### litter_records

```text
id
date
farm_id
house_id
flock_id optional
material
quantity_added
unit
condition: dry / damp / wet / dirty
ammonia_smell: none / mild / strong
action: added / replaced / removed / full_cleanout
notes
created_by
created_at
updated_at
```

### sanitation_tasks

```text
id
date
farm_id
house_id optional
task_type: nest_cleaning / feeder_cleaning / drinker_cleaning / water_line_flush / disinfection / footbath_change / rodent_control / other
item_used_id optional
quantity_used
performed_by
next_due_date
notes
created_by
created_at
updated_at
```

### environment_records

```text
id
date
farm_id
house_id
flock_id optional
temperature_high
temperature_low
temperature_average
humidity
ammonia_ppm
light_hours
power_outage
weather_notes
ventilation_status: good / fair / poor
notes
created_by
created_at
updated_at
```

### equipment

```text
id
farm_id
house_id optional
name
equipment_type: drinker / feeder / fan / light / timer / pump / scale / generator / other
purchase_date
status: active / needs_repair / retired
notes
created_at
updated_at
```

### maintenance_records

```text
id
equipment_id
date
issue
action_taken
parts_used
cost
performed_by
next_service_date
notes
created_by
created_at
updated_at
```

---

## 7.9 Sales and Expenses

### customers

```text
id
name
phone
email
address
customer_type: retail / wholesale / store / restaurant / family / other
default_price_level
notes
active
created_at
updated_at
```

### sales_orders

```text
id
date
customer_id
status: draft / confirmed / delivered / paid / cancelled
delivery_method: pickup / delivery
delivery_date
subtotal
discount
tax
total
amount_paid
balance_due
notes
created_by
created_at
updated_at
```

### sales_order_items

```text
id
sales_order_id
product_type: eggs
egg_grade_id
quantity
unit: egg / dozen / tray / carton / case
quantity_in_eggs
unit_price
line_total
flock_id optional
inventory_movement_id optional
created_at
updated_at
```

### payments

```text
id
sales_order_id
customer_id
payment_date
amount
method: cash / check / card / bank_transfer / mobile_payment / other
reference_number
notes
created_by
created_at
```

### expenses

```text
id
date
farm_id
house_id optional
flock_id optional
supplier_id optional
category: feed / medicine / labor / utilities / packaging / transport / repair / bedding / sanitation / other
description
amount
payment_method
receipt_attachment_id optional
notes
created_by
created_at
updated_at
```

---

## 7.10 Tasks, Alerts, Attachments, Audit Log

### tasks

```text
id
farm_id
house_id optional
flock_id optional
title
description
due_date
assigned_to
status: open / completed / cancelled
priority: low / medium / high
recurrence
completed_at
notes
created_by
created_at
updated_at
```

### alerts

```text
id
farm_id
house_id optional
flock_id optional
alert_type
severity: info / warning / critical
message
triggered_at
status: active / dismissed / resolved
related_record_type
related_record_id
created_at
updated_at
```

### attachments

```text
id
entity_type
entity_id
file_name
file_type
file_size
storage_path
uploaded_by
created_at
```

### audit_logs

```text
id
user_id
action: create / update / delete / login / export
entity_type
entity_id
old_values
new_values
ip_address optional
created_at
```

---

## 8. Wireframes

These are low-fidelity text wireframes. They describe layout and key controls, not final visual design.

---

## 8.1 Login Screen

```text
+--------------------------------------------------+
| Egg Farm Manager                                 |
|--------------------------------------------------|
| Email                                            |
| [______________________________________________] |
| Password                                         |
| [______________________________________________] |
|                                                  |
| [ Log In ]                                      |
|                                                  |
| Forgot password?                                |
+--------------------------------------------------+
```

---

## 8.2 Main Dashboard

```text
+--------------------------------------------------------------------------------+
| Egg Farm Manager                         Farm: [Main Farm v]   User: Maria      |
+--------------------------------------------------------------------------------+
| Sidebar                  | Dashboard                                           |
|--------------------------|-----------------------------------------------------|
| Dashboard                | [Eggs Today] [Saleable Eggs] [Hen-Day %] [Alerts]   |
| Daily Entry              | [Feed Used]  [Water Used]   [Sales Today] [Profit]  |
| Flocks                   |                                                     |
| Egg Inventory            | Egg Production Trend                                |
| Feed & Inventory         | +-------------------------------------------------+ |
| Water                    | | line chart                                       | |
| Health                   | +-------------------------------------------------+ |
| Beak Treatment           |                                                     |
| Sales                    | Active Alerts                                      |
| Expenses                 | +-------------------------------------------------+ |
| Reports                  | | Critical: Medication withdrawal active          | |
| Settings                 | | Warning: Feed stock low                         | |
|                          | | Warning: Egg production dropped 12%             | |
|                          | +-------------------------------------------------+ |
|                          |                                                     |
|                          | Tasks Due Today                                    |
|                          | +-------------------------------------------------+ |
|                          | | Clean drinkers - House A                        | |
|                          | | Vaccination follow-up - Flock B                 | |
|                          | +-------------------------------------------------+ |
+--------------------------------------------------------------------------------+
```

---

## 8.3 Farms Screen

```text
+--------------------------------------------------------------------------------+
| Farms                                                        [ + Add Farm ]      |
+--------------------------------------------------------------------------------+
| Search farms [____________________________]                                     |
|                                                                                |
| Farm Name        Location          Houses   Active Flocks   Status   Actions    |
| Main Farm        Riverside         4        3               Active   View Edit  |
| North Farm       Hill Road         2        1               Active   View Edit  |
| Old Farm         -                 1        0               Inactive View       |
+--------------------------------------------------------------------------------+
```

### Add/Edit Farm Form

```text
+----------------------------------------------+
| Add Farm                                     |
+----------------------------------------------+
| Farm Name        [_________________________] |
| Owner Name       [_________________________] |
| Address          [_________________________] |
| Phone            [_________________________] |
| Email            [_________________________] |
| Currency         [USD v]                     |
| Unit System      [Imperial v]                |
| Timezone         [America/Los_Angeles v]     |
| Notes            [_________________________] |
|                                              |
| [Cancel]                         [Save Farm] |
+----------------------------------------------+
```

---

## 8.4 Houses Screen

```text
+--------------------------------------------------------------------------------+
| Houses / Coops                                      Farm: [Main Farm v] [ + Add ]|
+--------------------------------------------------------------------------------+
| House Name     Type          Capacity   Active Flock      Current Birds Actions |
| House A        Deep litter   500        Layer Batch A     480           View    |
| House B        Cage          1000       Layer Batch B     950           View    |
| Pullet House   Brooding      300        Pullet Batch C    280           View    |
+--------------------------------------------------------------------------------+
```

### House Detail

```text
+--------------------------------------------------------------------------------+
| House A                                   [Edit] [Deactivate]                   |
+--------------------------------------------------------------------------------+
| Type: Deep litter        Capacity: 500       Current birds: 480                 |
| Active flock: Layer Batch A                                                       |
|                                                                                |
| Tabs: [Overview] [Daily Records] [Environment] [Litter] [Maintenance]           |
|                                                                                |
| Overview                                                                       |
| - 7-day eggs average: 390/day                                                  |
| - 7-day feed average: 110 lb/day                                               |
| - 7-day water average: 82 gal/day                                              |
| - Latest litter condition: Dry                                                 |
| - Active alerts: 1                                                             |
+--------------------------------------------------------------------------------+
```

---

## 8.5 Flocks Screen

```text
+--------------------------------------------------------------------------------+
| Flocks                                                   [ + Add Flock ]         |
+--------------------------------------------------------------------------------+
| Filters: Farm [Main v] Stage [All v] Status [Active v] Search [________]        |
|                                                                                |
| Flock Name      House     Breed        Age    Birds   Stage    Hen-Day  Actions |
| Layer Batch A   House A   ISA Brown    31 wk  480     Laying   81.2%    View    |
| Layer Batch B   House B   Hy-Line      45 wk  950     Laying   76.8%    View    |
| Pullet C        Pullet    Lohmann      10 wk  280     Growing  -        View    |
+--------------------------------------------------------------------------------+
```

### Add/Edit Flock Form

```text
+-----------------------------------------------------+
| Add Flock                                           |
+-----------------------------------------------------+
| Flock Name              [_________________________] |
| Farm                    [Main Farm v]               |
| House                   [House A v]                 |
| Breed/Strain            [ISA Brown]                 |
| Supplier                [Supplier v]                |
| Placement Date          [2026-06-01]                |
| Starting Bird Count     [500]                       |
| Bird Type               [Layer v]                   |
| Production Stage        [Growing v]                 |
| Expected Start Lay Date [2026-10-01]                |
| Expected Depletion Date [2028-01-01]                |
| Notes                   [_________________________] |
|                                                     |
| [Cancel]                                [Save Flock]|
+-----------------------------------------------------+
```

### Flock Detail

```text
+--------------------------------------------------------------------------------+
| Layer Batch A                              [Edit] [Move] [Deplete]              |
+--------------------------------------------------------------------------------+
| House: House A       Breed: ISA Brown       Age: 31 weeks       Birds: 480      |
| Stage: Laying        Status: Active         Beak Treated: Yes                  |
|                                                                                |
| KPI Cards                                                                       |
| [Hen-Day 81.2%] [Eggs 7-day avg 390] [Mortality 4.0%] [Feed/dozen $1.80]        |
|                                                                                |
| Tabs: [Overview] [Daily] [Eggs] [Feed/Water] [Health] [Procedures] [Financials] |
|                                                                                |
| Timeline                                                                       |
| - Today: 390 eggs, 110 lb feed, 82 gal water                                   |
| - Yesterday: Vitamin C additive started                                        |
| - 3 days ago: 1 mortality, reason unknown                                      |
+--------------------------------------------------------------------------------+
```

---

## 8.6 Daily Entry Screen

```text
+--------------------------------------------------------------------------------+
| Daily Entry                                      Date: [Today v] Farm: [Main v] |
+--------------------------------------------------------------------------------+
| House [House A v]     Flock [Layer Batch A v]      Current birds: 480          |
|                                                                                |
| Egg Production                                                                 |
| Total Eggs [____]  Good [____]  Cracked [____]  Dirty [____]  Soft [____]      |
| Small [____] Medium [____] Large [____] Jumbo [____] Internal [____] Discard [__]|
|                                                                                |
| Feed and Water                                                                 |
| Feed Item [Layer Mash v] Quantity [____] Unit [lb v]                           |
| Water Quantity [____] Unit [gal v]  Meter Start [____] Meter End [____]        |
|                                                                                |
| Mortality and Culling                                                          |
| Deaths [__] Culls [__] Reason [Unknown v]                                      |
|                                                                                |
| Environment                                                                    |
| Temp High [__] Temp Low [__] Humidity [__] Light Hours [__]                    |
|                                                                                |
| Notes                                                                          |
| [__________________________________________________________________________]   |
|                                                                                |
| [Save Draft]                                      [Save Daily Entry]            |
+--------------------------------------------------------------------------------+
```

### Daily Entry Validation State

```text
+--------------------------------------------------------------------------------+
| Warning                                                                        |
| Total eggs collected does not match category total.                             |
| Total: 390, Categories: 385                                                     |
|                                                                                |
| [Edit Counts] [Save Anyway With Manager Permission]                             |
+--------------------------------------------------------------------------------+
```

---

## 8.7 Egg Production Screen

```text
+--------------------------------------------------------------------------------+
| Egg Production                                                [ + Add Record ]  |
+--------------------------------------------------------------------------------+
| Filters: Date [This Week v] Farm [Main v] House [All v] Flock [All v]           |
|                                                                                |
| Date       Flock          Total Good Cracked Dirty Soft Hen-Day  Actions        |
| Jun 26     Layer Batch A  390   370  8       7     5    81.2%    View Edit      |
| Jun 26     Layer Batch B  720   690  15      10    5    75.8%    View Edit      |
| Jun 25     Layer Batch A  388   371  6       8     3    80.8%    View Edit      |
|                                                                                |
| Summary: Total 1,498 eggs | Saleable 1,431 | Loss 4.5%                         |
+--------------------------------------------------------------------------------+
```

---

## 8.8 Egg Inventory Screen

```text
+--------------------------------------------------------------------------------+
| Egg Inventory                                  [Adjust] [Discard] [Transfer]    |
+--------------------------------------------------------------------------------+
| Filters: Grade [All v] Location [All v] Age [All v]                             |
|                                                                                |
| Grade    Available Eggs   Dozens   Trays   Oldest Batch   Actions              |
| Small    240              20.0     8.0     Jun 24         View                 |
| Medium   600              50.0     20.0    Jun 25         View                 |
| Large    1,200            100.0    40.0    Jun 25         View                 |
| Jumbo    180              15.0     6.0     Jun 26         View                 |
| Cracked  35               2.9      1.2     Jun 26         Discard              |
|                                                                                |
| Total Saleable: 2,220 eggs / 185 dozen / 74 trays                              |
+--------------------------------------------------------------------------------+
```

### Inventory Movement History

```text
+--------------------------------------------------------------------------------+
| Egg Inventory Movements                                                        |
+--------------------------------------------------------------------------------+
| Date       Grade   Movement    Qty    Unit   Source/Reference       User       |
| Jun 26     Large   Production  +600   eggs   Layer Batch A          Ana        |
| Jun 26     Large   Sale        -300   eggs   Sale #1024             Maria      |
| Jun 26     Cracked Discard     -20    eggs   Discard record         Ana        |
+--------------------------------------------------------------------------------+
```

---

## 8.9 Feed Inventory Screen

```text
+--------------------------------------------------------------------------------+
| Feed Inventory                                      [ + Purchase ] [ + Item ]    |
+--------------------------------------------------------------------------------+
| Item           Stock     Unit   Days Left   Avg Cost   Expiring Soon   Actions  |
| Layer Mash     2,400     lb     18          $0.36/lb   No              View     |
| Grower Feed    800       lb     25          $0.32/lb   No              View     |
| Scratch Grain  120       lb     5           $0.28/lb   No              View     |
|                                                                                |
| Alerts: Scratch Grain low                                                       |
+--------------------------------------------------------------------------------+
```

### Feed Usage Screen

```text
+--------------------------------------------------------------------------------+
| Feed Usage                                                  [ + Add Usage ]      |
+--------------------------------------------------------------------------------+
| Date       Flock          Feed Item    Qty    Cost    Feed/Bird   Actions       |
| Jun 26     Layer Batch A  Layer Mash   110 lb $39.60  0.229 lb    View Edit     |
| Jun 26     Layer Batch B  Layer Mash   215 lb $77.40  0.226 lb    View Edit     |
+--------------------------------------------------------------------------------+
```

---

## 8.10 General Inventory Screen

```text
+--------------------------------------------------------------------------------+
| Inventory                                      [ + Item ] [ + Purchase ]         |
+--------------------------------------------------------------------------------+
| Category [All v] Search [________________] Low Stock [ ] Expiring Soon [ ]      |
|                                                                                |
| Item             Category       Stock    Unit   Expiry      Status    Actions   |
| Vitamin C        Vitamin        2.0      kg     2027-01-01  OK        View      |
| Electrolytes     Supplement     1.5      kg     2026-09-01  OK        View      |
| Vaccine ND       Vaccine        4        vials  2026-07-15  Expiring  View      |
| Egg Cartons      Packaging      320      each   -           OK        View      |
| Pine Shavings    Bedding        18       bags   -           OK        View      |
+--------------------------------------------------------------------------------+
```

### Inventory Item Detail

```text
+--------------------------------------------------------------------------------+
| Vitamin C                                      [Edit] [Add Purchase] [Use Item]  |
+--------------------------------------------------------------------------------+
| Category: Vitamin       Unit: kg       Requires Expiry: Yes       Active: Yes   |
| Supplier: Valley Feed   Default cost: $35/kg                                    |
|                                                                                |
| Lots                                                                           |
| Lot #       Expiry       Qty       Unit Cost      Location                      |
| VC-2026-05  2027-01-01   2.0 kg    $35.00        Store Room                    |
|                                                                                |
| Recent Movements                                                               |
| Jun 26  Usage      -0.1 kg   Layer Batch A   Heat stress                       |
| Jun 20  Purchase   +2.1 kg   Valley Feed                                        |
+--------------------------------------------------------------------------------+
```

---

## 8.11 Water Usage Screen

```text
+--------------------------------------------------------------------------------+
| Water Usage                                                [ + Add Record ]      |
+--------------------------------------------------------------------------------+
| Filters: Date [This Week v] House [All v] Flock [All v]                         |
|                                                                                |
| Date       Flock          Water    Unit   Water/Bird   Feed:Water   Actions     |
| Jun 26     Layer Batch A  82       gal    0.171 gal    1:2.98       View        |
| Jun 26     Layer Batch B  161      gal    0.169 gal    1:2.99       View        |
|                                                                                |
| Trend: 7-day water usage line chart                                             |
+--------------------------------------------------------------------------------+
```

### Water Quality Screen

```text
+--------------------------------------------------------------------------------+
| Water Quality Tests                                      [ + Add Test ]          |
+--------------------------------------------------------------------------------+
| Date       Source     pH   Chlorine  TDS   Bacteria   Treatment     Actions     |
| Jun 26     Well       6.8  2 ppm     450   Pass       Chlorine      View        |
| Jun 10     Well       7.1  1 ppm     470   Pass       None          View        |
+--------------------------------------------------------------------------------+
```

---

## 8.12 Additives and Supplements Screen

```text
+--------------------------------------------------------------------------------+
| Additives & Supplements                              [ + Add Application ]       |
+--------------------------------------------------------------------------------+
| Active Applications                                                            |
| Flock          Item        Method   Dosage           Reason       Ends          |
| Layer Batch A  Vitamin C   Water    1 g/gal          Heat stress  Jun 28        |
|                                                                                |
| History                                                                        |
| Date       Flock          Item          Qty Used   Reason        User           |
| Jun 26     Layer Batch A  Vitamin C     0.1 kg     Heat stress   Ana            |
| Jun 20     Layer Batch B  Calcium Mix   2 lb       Shell quality Maria          |
+--------------------------------------------------------------------------------+
```

### Additive Application Form

```text
+--------------------------------------------------------+
| Add Supplement / Additive Application                  |
+--------------------------------------------------------+
| Flock                  [Layer Batch A v]               |
| Item                   [Vitamin C v]                   |
| Lot                    [VC-2026-05 v]                  |
| Start Date             [2026-06-26]                    |
| End Date               [2026-06-28]                    |
| Delivery Method        [Water v]                       |
| Dosage Amount          [1]                             |
| Dosage Unit            [gram v]                        |
| Dosage Basis           [per gallon v]                  |
| Quantity Used          [0.1] [kg v]                    |
| Reason                 [Heat stress v]                 |
| Notes                  [_____________________________] |
|                                                        |
| [Cancel]                                      [Save]   |
+--------------------------------------------------------+
```

---

## 8.13 Medication Screen

```text
+--------------------------------------------------------------------------------+
| Medication Applications                              [ + Add Medication ]        |
+--------------------------------------------------------------------------------+
| Active Treatments                                                              |
| Flock          Medication  Start   End     Withdrawal Until   Status            |
| Layer Batch B  Amprolium   Jun 24  Jun 28  Jul 05             Withdrawal Active |
|                                                                                |
| History                                                                        |
| Date       Flock          Medication   Reason       Authorized By  Actions      |
| Jun 24     Layer Batch B  Amprolium    Coccidiosis  Dr. Smith      View         |
+--------------------------------------------------------------------------------+
```

### Medication Form

```text
+--------------------------------------------------------+
| Add Medication Application                             |
+--------------------------------------------------------+
| Flock                  [Layer Batch B v]               |
| Medication             [Amprolium v]                   |
| Lot                    [AM-8891 v]                     |
| Start Date             [2026-06-24]                    |
| End Date               [2026-06-28]                    |
| Delivery Method        [Water v]                       |
| Dosage                 [10] [ml] [per gallon v]        |
| Reason                 [Suspected coccidiosis]         |
| Diagnosis              [_____________________________] |
| Authorized By          [Dr. Smith]                     |
| Egg Withdrawal Until   [2026-07-05]                    |
| Notes                  [_____________________________] |
|                                                        |
| [Cancel]                                      [Save]   |
+--------------------------------------------------------+
```

---

## 8.14 Vaccination Screen

```text
+--------------------------------------------------------------------------------+
| Vaccinations                                      [ + Add Vaccination ]          |
+--------------------------------------------------------------------------------+
| Upcoming Due                                                                  |
| Flock          Disease      Due Date     Vaccine      Status                    |
| Pullet C       Fowl Pox     Jul 10       Fowl Pox     Upcoming                  |
|                                                                                |
| History                                                                        |
| Date       Flock          Disease       Method       Birds   Next Due  Actions  |
| Jun 12     Pullet C       Newcastle     Water        280     -         View     |
+--------------------------------------------------------------------------------+
```

---

## 8.15 Mortality and Culling Screen

```text
+--------------------------------------------------------------------------------+
| Mortality & Culling                                  [ + Add Record ]            |
+--------------------------------------------------------------------------------+
| Filters: Date [This Month v] Flock [All v] Reason [All v]                       |
|                                                                                |
| Date       Flock          Deaths  Culls  Reason      Current Birds  Actions     |
| Jun 26     Layer Batch A  1       0      Unknown     480            View        |
| Jun 25     Layer Batch B  0       2      Injury      950            View        |
|                                                                                |
| Summary: Deaths 12 | Culls 5 | Mortality 1.2%                                   |
+--------------------------------------------------------------------------------+
```

---

## 8.16 Health and Welfare Screen

```text
+--------------------------------------------------------------------------------+
| Health & Welfare                                  [ + Health Event ]             |
+--------------------------------------------------------------------------------+
| Filters: Flock [All v] Severity [All v] Type [All v]                            |
|                                                                                |
| Date       Flock          Type          Severity   Summary             Actions  |
| Jun 26     Layer Batch A  Welfare       Medium     Feather pecking     View     |
| Jun 22     Layer Batch B  Vet Visit     High       Respiratory signs   View     |
|                                                                                |
| Follow-Ups Due                                                                 |
| - Layer Batch B respiratory follow-up due Jun 29                                |
+--------------------------------------------------------------------------------+
```

### Health Event Form

```text
+--------------------------------------------------------+
| Add Health / Welfare Event                             |
+--------------------------------------------------------+
| Flock             [Layer Batch A v]                    |
| Date              [2026-06-26]                         |
| Event Type        [Welfare Issue v]                    |
| Severity          [Medium v]                           |
| Symptoms/Issue    [Feather pecking observed]           |
| Diagnosis         [_____________________________]      |
| Action Taken      [Added enrichment, monitor flock]    |
| Vet Name          [_____________________________]      |
| Follow-Up Date    [2026-06-29]                         |
| Attachments       [Upload files]                       |
| Notes             [_____________________________]      |
|                                                        |
| [Cancel]                                      [Save]   |
+--------------------------------------------------------+
```

---

## 8.17 Beak Treatment Screen

```text
+--------------------------------------------------------------------------------+
| Beak Treatment                                   [ + Add Treatment ]             |
+--------------------------------------------------------------------------------+
| Flock: [Layer Batch A v]                                                          |
|                                                                                |
| Treatment Status                                                               |
| Beak Treated: Yes | Method: Infrared | Date: 2026-02-01 | Follow-up: Complete   |
|                                                                                |
| History                                                                        |
| Date       Age     Birds  Method     Type        Operator      Actions          |
| Feb 01     7 days  500    Infrared   First trim  Hatchery      View             |
|                                                                                |
| Follow-Ups                                                                     |
| Date       Feed Intake  Water Intake  Mortality  Uniformity   Actions           |
| Feb 08     Normal       Normal        0          Good         View              |
+--------------------------------------------------------------------------------+
```

### Beak Treatment Form

```text
+--------------------------------------------------------+
| Add Beak Treatment                                     |
+--------------------------------------------------------+
| Flock                  [Layer Batch A v]               |
| Date Performed         [2026-02-01]                    |
| Bird Age Days          [7]                             |
| Birds Treated          [500]                           |
| Method                 [Infrared v]                    |
| Trim Type              [First Trim v]                  |
| Upper Beak Amount      [Light]                         |
| Lower Beak Amount      [None]                          |
| Operator               [Hatchery]                      |
| Equipment Used         [_____________________________] |
| Reason                 [Routine pullet management]     |
| Immediate Mortality    [0]                             |
| Notes                  [_____________________________] |
|                                                        |
| [Cancel]                           [Save Treatment]    |
+--------------------------------------------------------+
```

### Follow-Up Form

```text
+--------------------------------------------------------+
| Add Beak Treatment Follow-Up                           |
+--------------------------------------------------------+
| Follow-Up Date         [2026-02-08]                    |
| Feed Intake Status     [Normal v]                      |
| Water Intake Status    [Normal v]                      |
| Mortality Count        [0]                             |
| Pecking Observed       [No v]                          |
| Cannibalism Observed   [No v]                          |
| Uniformity Rating      [Good v]                        |
| Corrective Action      [None]                          |
| Notes                  [_____________________________] |
|                                                        |
| [Cancel]                                      [Save]   |
+--------------------------------------------------------+
```

---

## 8.18 Litter and Sanitation Screen

```text
+--------------------------------------------------------------------------------+
| Litter & Sanitation                              [ + Litter ] [ + Sanitation ]   |
+--------------------------------------------------------------------------------+
| Litter Records                                                                 |
| Date       House    Action       Material       Condition  Ammonia   Actions    |
| Jun 26     House A  Added        Pine shavings  Dry        None      View       |
| Jun 20     House B  Full clean   Pine shavings  Dirty      Mild      View       |
|                                                                                |
| Sanitation Tasks                                                               |
| Date       House    Task              Item Used      Next Due    Actions        |
| Jun 26     House A  Drinker cleaning  Sanitizer      Jul 03      View           |
| Jun 25     House B  Footbath change   Disinfectant   Jun 28      View           |
+--------------------------------------------------------------------------------+
```

---

## 8.19 Environment and Lighting Screen

```text
+--------------------------------------------------------------------------------+
| Environment & Lighting                              [ + Add Record ]            |
+--------------------------------------------------------------------------------+
| Filters: Date [This Week v] House [All v]                                     |
|                                                                                |
| Date       House    Temp High  Temp Low  Humidity  Light Hrs  Ventilation       |
| Jun 26     House A  92°F       70°F      70%       16         Good              |
| Jun 26     House B  90°F       69°F      68%       16         Fair              |
|                                                                                |
| Correlation View                                                               |
| [Egg Production Trend] overlaid with [Temperature]                              |
+--------------------------------------------------------------------------------+
```

---

## 8.20 Equipment and Maintenance Screen

```text
+--------------------------------------------------------------------------------+
| Equipment                                              [ + Add Equipment ]       |
+--------------------------------------------------------------------------------+
| Name              House    Type      Status        Next Service      Actions     |
| Water Pump A      House A  Pump      Active        Jul 26            View        |
| Fan 1             House B  Fan       Needs Repair  Jun 29            View        |
| Generator         Farm     Generator Active        Aug 01            View        |
+--------------------------------------------------------------------------------+
```

### Equipment Detail

```text
+--------------------------------------------------------------------------------+
| Water Pump A                                     [Edit] [Add Maintenance]        |
+--------------------------------------------------------------------------------+
| Type: Pump      House: House A      Status: Active                              |
|                                                                                |
| Maintenance History                                                            |
| Date       Issue          Action Taken       Cost      Next Service   Actions   |
| Jun 26     Low pressure   Replaced filter    $22.00    Jul 26         View      |
+--------------------------------------------------------------------------------+
```

---

## 8.21 Customers Screen

```text
+--------------------------------------------------------------------------------+
| Customers                                                [ + Add Customer ]      |
+--------------------------------------------------------------------------------+
| Search [____________________] Type [All v]                                      |
|                                                                                |
| Name              Type        Phone        Balance Due    Last Order   Actions  |
| Local Market      Store       555-1111     $120.00        Jun 26       View     |
| Smith Family      Retail      555-2222     $0.00          Jun 20       View     |
| Sunrise Cafe      Restaurant  555-3333     $75.00         Jun 24       View     |
+--------------------------------------------------------------------------------+
```

### Customer Detail

```text
+--------------------------------------------------------------------------------+
| Local Market                                      [Edit] [New Sale] [Payment]    |
+--------------------------------------------------------------------------------+
| Type: Store       Phone: 555-1111       Balance: $120.00                        |
|                                                                                |
| Recent Orders                                                                  |
| Date       Order #   Total     Paid      Balance    Status       Actions        |
| Jun 26     1024      $240.00   $120.00   $120.00    Delivered    View           |
| Jun 19     1012      $220.00   $220.00   $0.00      Paid         View           |
+--------------------------------------------------------------------------------+
```

---

## 8.22 Sales Screen

```text
+--------------------------------------------------------------------------------+
| Sales Orders                                           [ + New Sale ]            |
+--------------------------------------------------------------------------------+
| Filters: Date [This Month v] Customer [All v] Status [All v]                    |
|                                                                                |
| Date       Order # Customer       Total    Paid    Balance  Status     Actions  |
| Jun 26     1024    Local Market   $240.00  $120.00 $120.00  Delivered  View     |
| Jun 25     1023    Smith Family   $28.00   $28.00  $0.00    Paid       View     |
+--------------------------------------------------------------------------------+
```

### New Sale Form

```text
+--------------------------------------------------------------------------------+
| New Egg Sale                                                                    |
+--------------------------------------------------------------------------------+
| Customer [Local Market v]     Date [2026-06-26]     Delivery [Delivery v]       |
|                                                                                |
| Items                                                                          |
| Grade [Large v] Qty [20] Unit [Tray v] Unit Price [$8.00] Line [$160.00] [x]    |
| Grade [Medium v] Qty [10] Unit [Tray v] Unit Price [$7.00] Line [$70.00] [x]    |
| [ + Add Item ]                                                                 |
|                                                                                |
| Subtotal: $230.00                                                              |
| Discount: [$0.00]                                                              |
| Tax:      [$0.00]                                                              |
| Total:    $230.00                                                              |
| Paid:     [$100.00] Method [Cash v]                                            |
| Balance:  $130.00                                                              |
|                                                                                |
| Notes [___________________________________________________________________]    |
|                                                                                |
| [Cancel]                                      [Save Sale] [Save & Print]        |
+--------------------------------------------------------------------------------+
```

### Withdrawal Warning on Sale

```text
+--------------------------------------------------------------------------------+
| Critical Warning                                                                |
| Layer Batch B has an active medication withdrawal until 2026-07-05.             |
| Eggs linked to this flock cannot be sold.                                       |
|                                                                                |
| [Remove Restricted Eggs] [Manager Override]                                     |
+--------------------------------------------------------------------------------+
```

---

## 8.23 Expenses Screen

```text
+--------------------------------------------------------------------------------+
| Expenses                                                [ + Add Expense ]        |
+--------------------------------------------------------------------------------+
| Filters: Date [This Month v] Category [All v] Flock [All v]                    |
|                                                                                |
| Date       Category   Description        Amount   Flock          Actions        |
| Jun 26     Feed       Layer mash         $720.00  All            View           |
| Jun 25     Repair     Fan repair         $85.00   House B        View           |
| Jun 24     Packaging  Egg cartons        $120.00  -              View           |
|                                                                                |
| Total Expenses This Month: $2,450.00                                            |
+--------------------------------------------------------------------------------+
```

### Add Expense Form

```text
+--------------------------------------------------------+
| Add Expense                                            |
+--------------------------------------------------------+
| Date              [2026-06-26]                         |
| Category          [Feed v]                             |
| Description       [Layer mash purchase]                |
| Amount            [$720.00]                            |
| Supplier          [Valley Feed v]                      |
| Farm              [Main Farm v]                        |
| House             [Optional v]                         |
| Flock             [Optional v]                         |
| Payment Method    [Card v]                             |
| Receipt           [Upload]                             |
| Notes             [_____________________________]      |
|                                                        |
| [Cancel]                                      [Save]   |
+--------------------------------------------------------+
```

---

## 8.24 Tasks and Alerts Screen

```text
+--------------------------------------------------------------------------------+
| Tasks & Alerts                                         [ + Add Task ]            |
+--------------------------------------------------------------------------------+
| Tabs: [Active Alerts] [Tasks] [Resolved Alerts]                                  |
|                                                                                |
| Active Alerts                                                                  |
| Severity   Alert                         Flock/House      Triggered   Actions   |
| Critical   Medication withdrawal active  Layer Batch B    Jun 24      View      |
| Warning    Feed stock low                Scratch Grain    Jun 26      Resolve   |
| Warning    Egg production dropped 12%    Layer Batch A    Jun 26      View      |
|                                                                                |
| Tasks Due                                                                      |
| Due Date   Task                      Assigned To   Priority   Status   Actions  |
| Jun 26     Clean drinkers House A    Ana           Medium     Open     Done     |
| Jun 29     Health follow-up Batch B  Dr. Smith     High       Open     View     |
+--------------------------------------------------------------------------------+
```

---

## 8.25 Reports Screen

```text
+--------------------------------------------------------------------------------+
| Reports                                                                         |
+--------------------------------------------------------------------------------+
| Production Reports              Financial Reports           Inventory Reports   |
| - Daily Egg Production          - Sales Report              - Feed Inventory    |
| - Weekly Egg Production         - Expense Report            - Feed Usage        |
| - Egg Grade Breakdown           - Profit & Loss             - Expiring Items    |
| - Hen-Day Production            - Profit per Dozen          - Packaging Stock   |
| - Flock Performance             - Customer Balances         - Stock Movements   |
|                                                                                |
| Health Reports                  Operations Reports                              |
| - Mortality                     - Tasks                                      |
| - Medication                    - Maintenance                                 |
| - Vaccination                   - Sanitation                                  |
| - Beak Treatment                - Environment                                 |
+--------------------------------------------------------------------------------+
```

### Production Report Wireframe

```text
+--------------------------------------------------------------------------------+
| Production Report                                                               |
+--------------------------------------------------------------------------------+
| Date Range [This Month v] Farm [Main v] House [All v] Flock [All v] [Run]       |
|                                                                                |
| KPI Cards                                                                       |
| [Total Eggs] [Saleable Eggs] [Hen-Day Avg] [Egg Loss %] [Best Flock]            |
|                                                                                |
| Chart: Egg Production Trend                                                     |
| +----------------------------------------------------------------------------+ |
| | line chart                                                                  | |
| +----------------------------------------------------------------------------+ |
|                                                                                |
| Table                                                                          |
| Date       Flock          Total Eggs  Saleable  Loss %  Hen-Day %              |
| Jun 26     Layer Batch A  390         370       5.1%    81.2%                  |
|                                                                                |
| [Export CSV] [Export Excel] [Print]                                             |
+--------------------------------------------------------------------------------+
```

### Profitability Report Wireframe

```text
+--------------------------------------------------------------------------------+
| Profitability Report                                                            |
+--------------------------------------------------------------------------------+
| Date Range [This Month v] Farm [Main v] Flock [All v] [Run]                    |
|                                                                                |
| [Revenue] [Expenses] [Profit] [Profit/Dozen] [Feed Cost/Dozen]                  |
|                                                                                |
| Revenue vs Expense Chart                                                        |
| +----------------------------------------------------------------------------+ |
| | bar chart                                                                   | |
| +----------------------------------------------------------------------------+ |
|                                                                                |
| Flock Breakdown                                                                |
| Flock          Revenue   Feed   Medicine   Packaging   Other   Profit          |
| Layer Batch A  $3,200   $1,200 $80        $150        $300    $1,470          |
|                                                                                |
| [Export CSV] [Export Excel] [Print]                                             |
+--------------------------------------------------------------------------------+
```

---

## 8.26 Settings Screen

```text
+--------------------------------------------------------------------------------+
| Settings                                                                        |
+--------------------------------------------------------------------------------+
| Tabs: [General] [Units] [Egg Grades] [Packaging] [Alerts] [Users] [Import/Export]|
|                                                                                |
| General                                                                        |
| Default Farm: [Main Farm v]                                                    |
| Currency:     [USD v]                                                          |
| Timezone:     [America/Los_Angeles v]                                          |
| Unit System:  [Imperial v]                                                     |
|                                                                                |
| [Save Settings]                                                                |
+--------------------------------------------------------------------------------+
```

### Egg Grade Settings

```text
+--------------------------------------------------------------------------------+
| Egg Grades                                                    [ + Add Grade ]    |
+--------------------------------------------------------------------------------+
| Name       Saleable   Sort Order   Active   Actions                              |
| Small      Yes        1            Yes      Edit                                 |
| Medium     Yes        2            Yes      Edit                                 |
| Large      Yes        3            Yes      Edit                                 |
| Jumbo      Yes        4            Yes      Edit                                 |
| Cracked    No         5            Yes      Edit                                 |
| Dirty      No         6            Yes      Edit                                 |
+--------------------------------------------------------------------------------+
```

### Packaging Settings

```text
+--------------------------------------------------------------------------------+
| Packaging Units                                                                 |
+--------------------------------------------------------------------------------+
| Unit Name   Eggs Per Unit   Active   Actions                                    |
| Egg         1               Yes      Locked                                     |
| Dozen       12              Yes      Edit                                       |
| Tray        30              Yes      Edit                                       |
| Carton      12              Yes      Edit                                       |
| Case        360             Yes      Edit                                       |
+--------------------------------------------------------------------------------+
```

---

## 8.27 Users and Roles Screen

```text
+--------------------------------------------------------------------------------+
| Users                                                    [ + Add User ]          |
+--------------------------------------------------------------------------------+
| Name        Email              Role        Farm Access       Status    Actions   |
| Maria       maria@example.com  Manager     Main Farm         Active    Edit      |
| Ana         ana@example.com    Worker      House A, House B  Active    Edit      |
| Dr. Smith   vet@example.com    Vet         Main Farm         Active    Edit      |
+--------------------------------------------------------------------------------+
```

---

## 8.28 Audit Log Screen

```text
+--------------------------------------------------------------------------------+
| Audit Log                                                                       |
+--------------------------------------------------------------------------------+
| Filters: User [All v] Entity [All v] Action [All v] Date [This Week v]          |
|                                                                                |
| Time              User    Action   Entity              Summary          View     |
| Jun 26 09:02 AM   Ana     Create   Daily Entry         House A eggs     View     |
| Jun 26 10:14 AM   Maria   Update   Egg Production      390 -> 395       View     |
| Jun 26 11:20 AM   Maria   Export   Production Report   CSV export       View     |
+--------------------------------------------------------------------------------+
```

---

## 9. Alerts and Business Rules

## 9.1 Alert Rules

### Production Drop

Trigger when:

```text
Today hen-day production is more than X% below 7-day average.
Default X = 10%.
```

### Mortality Spike

Trigger when:

```text
Daily mortality exceeds configured threshold.
Default threshold = 0.5% of current live birds in one day.
```

### Water Intake Drop

Trigger when:

```text
Water per bird is more than X% below 7-day average.
Default X = 20%.
```

### Feed Intake Drop

Trigger when:

```text
Feed per bird is more than X% below 7-day average.
Default X = 15%.
```

### Low Feed Stock

Trigger when:

```text
Estimated days of feed remaining is below configured value.
Default = 7 days.
```

### Low Packaging Stock

Trigger when:

```text
Packaging stock is below configured reorder quantity.
```

### Medication Withdrawal Active

Trigger when:

```text
Current date is before egg_withdrawal_until for an active medication record.
```

### Expiring Inventory

Trigger when:

```text
Inventory lot expires within configured number of days.
Default = 30 days.
```

### Vaccination Due

Trigger when:

```text
Vaccination next_due_date is within configured reminder window.
Default = 7 days.
```

### Beak Treatment Follow-Up Due

Trigger when:

```text
Beak treatment record exists without follow-up within configured period.
Default = 7 days after treatment.
```

---

## 10. Reports and KPIs

## 10.1 Production KPIs

- Total eggs collected
- Saleable eggs
- Non-saleable eggs
- Hen-day production %
- Egg loss %
- Cracked egg %
- Dirty egg %
- Soft-shell egg %
- Dozens produced
- Trays produced
- Eggs per hen

## 10.2 Flock KPIs

- Current live birds
- Age days/weeks
- Cumulative mortality %
- Daily mortality %
- Average hen-day production
- Feed per bird per day
- Water per bird per day
- Feed cost per dozen
- Revenue per hen
- Profit per dozen

## 10.3 Inventory KPIs

- Feed stock remaining
- Days of feed remaining
- Egg stock by grade
- Egg stock age
- Packaging stock remaining
- Expiring medications/vaccines
- Supplement usage by flock

## 10.4 Financial KPIs

- Total revenue
- Total expenses
- Gross profit
- Profit per dozen
- Feed cost per dozen
- Packaging cost per dozen
- Revenue by customer
- Revenue by grade
- Customer balances

---

## 11. Import and Export

## 11.1 Import

The system should support CSV/Excel import for:

- Flocks
- Customers
- Suppliers
- Inventory items
- Egg production history
- Sales history
- Expenses

### Import Requirements

- Show mapping screen for columns.
- Validate required fields.
- Preview first rows before import.
- Show errors and allow download of failed rows.
- Audit import activity.

## 11.2 Export

The system should export:

- Reports
- Egg production records
- Inventory movements
- Sales
- Expenses
- Flock histories
- Customer balances

Export formats:

- CSV
- Excel/XLSX
- PDF in future phase

---

## 12. API Considerations

Future API endpoints should allow:

- Creating daily entries.
- Reading dashboard summaries.
- Importing sensor readings.
- Exporting reports.
- Integrating with accounting systems.
- Integrating mobile apps.

MVP can be built with internal APIs only, but API structure should be clean enough to expose later.

---

## 13. Suggested Navigation Structure

```text
Dashboard
Daily Entry
Farms & Houses
Flocks
Eggs
  Egg Production
  Egg Inventory
Inventory
  Feed
  General Inventory
  Purchases
Water
Health & Welfare
  Health Events
  Medication
  Vaccination
  Beak Treatment
  Mortality & Culling
Operations
  Litter & Sanitation
  Environment
  Equipment & Maintenance
Sales
  Customers
  Sales Orders
  Payments
Expenses
Tasks & Alerts
Reports
Settings
  General
  Egg Grades
  Packaging Units
  Users & Roles
  Import/Export
  Audit Log
```

---

## 14. MVP Acceptance Criteria

The MVP is complete when a user can:

1. Create a farm, houses, and flocks.
2. Enter daily egg production, feed usage, water usage, mortality, and notes.
3. Track egg inventory by grade.
4. Record feed and general inventory purchases and usage.
5. Record supplements, medications, vaccines, and withdrawal dates.
6. Record beak treatment and follow-up.
7. Record customers, egg sales, payments, and expenses.
8. See dashboard KPIs.
9. Receive alerts for low stock, production drops, mortality spikes, and withdrawal periods.
10. Generate production, inventory, mortality, sales, expense, and profitability reports.
11. Export key reports to CSV/Excel.
12. Manage users and roles.
13. View audit logs for important changes.

---

## 15. Build Phases

### Phase 1: Core MVP

- Authentication
- Users and roles
- Farm/house/flock setup
- Daily entry
- Egg production
- Egg inventory
- Feed inventory and usage
- Water usage
- Mortality and culling
- Basic health events
- Additives/supplements
- Medication/vaccination
- Beak treatment
- Customers
- Sales/payments
- Expenses
- Dashboard
- Basic reports
- CSV export

### Phase 2: Strong Operations

- Inventory lots and expiry
- Medication withdrawal enforcement
- Packaging inventory
- Water quality
- Environment tracking
- Litter/sanitation
- Equipment maintenance
- Tasks and recurring reminders
- Advanced reports
- Excel import/export
- Audit log improvements

### Phase 3: Commercial Features

- Offline mobile mode
- Barcode/QR support
- Accounting integration
- Delivery tracking
- IoT feed/water/environment sensor integrations
- Forecasting and benchmarking
- Multi-farm comparison
- API access
- Role-specific mobile workflows

---

## 16. Implementation Notes

### 16.1 Recommended Tech Approach

Any modern stack can support this system. Good options include:

- Backend: Django, Laravel, Rails, NestJS, or FastAPI
- Frontend: React, Vue, Svelte, or server-rendered templates
- Database: PostgreSQL
- Mobile: responsive web first, native app later if needed

### 16.2 Important Design Choices

- Use PostgreSQL for relational consistency.
- Use soft deletes for important records.
- Audit important changes.
- Store inventory movements as immutable ledger-style records when possible.
- Calculate stock from movements or maintain cached stock with reconciliation.
- Keep daily entry optimized for mobile.
- Make unit conversion explicit.
- Keep egg inventory in base unit of eggs internally.
- Keep feed inventory in base weight unit internally.

### 16.3 Recommended Internal Units

- Eggs: individual eggs
- Feed: grams or pounds, depending on system setting
- Water: liters or gallons, depending on system setting
- Money: integer cents
- Dates: store in UTC with farm timezone context where needed

---

## 17. Open Questions

These should be resolved before development:

1. Should the system support multiple farms from day one?
2. Should egg inventory track age by production date?
3. Should sales require selecting source flock, or only egg grade inventory?
4. Should medication withdrawal block sales completely or allow manager override?
5. Should workers be able to edit their own daily entries after submission?
6. Should daily entries be locked after a configurable number of days?
7. Should inventory stock be calculated from movements only, or stored as cached balances?
8. Should reports use fiscal periods or simple calendar periods?
9. Should the MVP be web-only or installable as a local/self-hosted app?
10. Should the system support offline entry in the first release?

---

## 18. Glossary

### Flock

A group of birds managed together, usually with the same breed, age, source, and placement date.

### House / Coop

A physical location where birds are kept.

### Hen-Day Production

A layer performance KPI calculated as eggs collected divided by current live hens, multiplied by 100.

### Saleable Eggs

Eggs that can be sold, usually after excluding cracked, dirty, soft-shell, discarded, or internal-use eggs.

### Withdrawal Period

A period after medication during which eggs or meat should not be sold or consumed, depending on the medication instructions.

### Beak Treatment

A flock procedure such as infrared beak treatment or beak trimming, tracked for welfare, performance, and compliance history.

### Inventory Movement

A record that changes inventory stock, such as purchase, usage, sale, adjustment, transfer, or discard.

---

## 19. Summary

Egg Farm Manager should start as a practical, fast daily record system for egg-producing poultry farms. The MVP should prioritize daily entry, flock performance, egg inventory, feed/water tracking, mortality, health records, sales, expenses, and reports. Enterprise-style features can be layered in later, but the foundation must be accurate flock-centered records and reliable inventory movements.

