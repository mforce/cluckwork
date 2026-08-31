// Typed labels for the app's closed vocabularies (#182, Task 4). This module is
// the ONLY sanctioned way to render an enum-like wire value as display text —
// screens call e.g. `statusLabel(o.status)`, never `t("enums:status." + value)`
// with a defaultValue, which would defeat compile-time checking and silently
// render a raw key or the English fallback.
//
// How the compile-time safety works, per family:
//   1. A `<FAMILY>_VALUES` tuple (`as const`) is the single source of the raw
//      wire vocabulary; the exported union type is derived from it.
//   2. A `<FAMILY>_KEYS` map is declared `as const satisfies Record<Union,
//      EnumsKey>`. `satisfies Record<Union, …>` makes a MISSING or STRAY member
//      a compile error (exhaustiveness, both directions). `EnumsKey` constrains
//      every value to a key that actually exists in `en.enums`, so if a screen
//      key is renamed/removed in en.ts, or the map points at a typo, THAT is a
//      compile error too — the catalog and the unions cannot drift apart.
//   3. The label function does `i18n.t(<FAMILY>_KEYS[value])`. Because the map
//      values keep their literal types (`as const`), the argument is a union of
//      real i18next keys, type-checked like any other `t(...)` call.
//
// Each helper accepts `<Union> | (string & {})`: the union drives autocomplete
// and lets callers pass an API `string` field WITHOUT an `as` cast, while a
// RUNTIME value outside the union (a server enum member the SPA hasn't caught up
// to) degrades to the raw string — the same passthrough the old raw-text render
// gave — instead of `i18n.t(undefined)` rendering BLANK. This is a runtime
// safety net ONLY; it does not weaken the compile-time coupling above (the
// `*_KEYS` maps stay `as const satisfies Record<Union, EnumsKey>`, so a static
// value outside the union is still a type error, and catalog↔union drift still
// fails typecheck both directions).
//
// Maintenance note: the compile-time guard is 1:1 between each union and its
// KEYS map, NOT against en.enums directly — a catalog-ONLY `enums` key with no
// union member (e.g. a stray key nothing maps to) would go uncaught here.
//
// `enums` IS in TRANSLATED_NAMESPACES: es/tl carry machine-drafted enum
// translations (#182, pending native review).
import { en } from "./en";
import i18n from "./index";
import { STATUS_VALUES } from "../components/StatusBadge";

// Every fully-qualified i18next key that resolves inside the `enums` namespace,
// e.g. "enums:status.Active". Constrains the KEYS maps below so a value can only
// be a key that en.enums actually defines.
type EnumsKey = `enums:${Extract<keyof typeof en.enums, string>}`;

// ---------------------------------------------------------------------------
// status — sourced from StatusBadge.STATUS_VALUES so the pill vocabulary and its
// labels are one thing. Identity labels except ManagerAdjusted -> "Adjusted".
// ---------------------------------------------------------------------------
export type StatusValue = (typeof STATUS_VALUES)[number];
const STATUS_KEYS = {
  Active: "enums:status.Active",
  Inactive: "enums:status.Inactive",
  Draft: "enums:status.Draft",
  Submitted: "enums:status.Submitted",
  Locked: "enums:status.Locked",
  ManagerAdjusted: "enums:status.ManagerAdjusted",
  Voided: "enums:status.Voided",
  Confirmed: "enums:status.Confirmed",
  Shipped: "enums:status.Shipped",
  Invoiced: "enums:status.Invoiced",
  Cancelled: "enums:status.Cancelled",
  Depleted: "enums:status.Depleted",
  Archived: "enums:status.Archived",
} as const satisfies Record<StatusValue, EnumsKey>;
export function statusLabel(value: StatusValue | (string & {})): string {
  const key = STATUS_KEYS[value as StatusValue];
  return key ? i18n.t(key) : String(value);
}

// NOTE: payment method and sale unit are deliberately NOT here. They already
// live in the TRANSLATED `sales` namespace (sales:method*, sales:unit*), which
// carries its own es/tl. `enums` is in TRANSLATED_NAMESPACES too (#182,
// pending native review), so adding them here would just be a redundant
// second copy of the same vocabulary, not a translation-coverage gain. Sale
// unit's only other render site (ProductsPage `{p.defaultUnit}`) shows the
// raw value today — nothing is lost by leaving it until a native pass
// decides on a shared source.

// ---------------------------------------------------------------------------
// role (UsersPage) — Roles.Assignable plus Worker (a user with no role row).
// ---------------------------------------------------------------------------
export const ROLE_VALUES = ["Worker", "Admin", "Manager", "Sales", "ReadOnly"] as const;
export type RoleValue = (typeof ROLE_VALUES)[number];
const ROLE_KEYS = {
  Worker: "enums:role.Worker",
  Admin: "enums:role.Admin",
  Manager: "enums:role.Manager",
  Sales: "enums:role.Sales",
  ReadOnly: "enums:role.ReadOnly",
} as const satisfies Record<RoleValue, EnumsKey>;
export function roleLabel(value: RoleValue | (string & {})): string {
  const key = ROLE_KEYS[value as RoleValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// water source (WaterPage) — WaterSource enum.
// ---------------------------------------------------------------------------
export const WATER_SOURCE_VALUES = ["Well", "Municipal", "Tank", "Other"] as const;
export type WaterSourceValue = (typeof WATER_SOURCE_VALUES)[number];
const WATER_SOURCE_KEYS = {
  Well: "enums:waterSource.Well",
  Municipal: "enums:waterSource.Municipal",
  Tank: "enums:waterSource.Tank",
  Other: "enums:waterSource.Other",
} as const satisfies Record<WaterSourceValue, EnumsKey>;
export function waterSourceLabel(value: WaterSourceValue | (string & {})): string {
  const key = WATER_SOURCE_KEYS[value as WaterSourceValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// water unit (WaterPage) — WaterUsage.AllowedUnits, a fixed 2-value set.
// ---------------------------------------------------------------------------
export const WATER_UNIT_VALUES = ["L", "gal"] as const;
export type WaterUnitValue = (typeof WATER_UNIT_VALUES)[number];
const WATER_UNIT_KEYS = {
  L: "enums:waterUnit.L",
  gal: "enums:waterUnit.gal",
} as const satisfies Record<WaterUnitValue, EnumsKey>;
export function waterUnitLabel(value: WaterUnitValue | (string & {})): string {
  const key = WATER_UNIT_KEYS[value as WaterUnitValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// grade type (GradesPage) — EggGradeType enum.
// ---------------------------------------------------------------------------
export const GRADE_TYPE_VALUES = ["Size", "Quality", "Custom"] as const;
export type GradeTypeValue = (typeof GRADE_TYPE_VALUES)[number];
const GRADE_TYPE_KEYS = {
  Size: "enums:gradeType.Size",
  Quality: "enums:gradeType.Quality",
  Custom: "enums:gradeType.Custom",
} as const satisfies Record<GradeTypeValue, EnumsKey>;
export function gradeTypeLabel(value: GradeTypeValue | (string & {})): string {
  const key = GRADE_TYPE_KEYS[value as GradeTypeValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// inventory category (InventoryPage) — InventoryCategory enum.
// ---------------------------------------------------------------------------
export const INVENTORY_CATEGORY_VALUES = [
  "Feed",
  "Supplement",
  "Additive",
  "Medication",
  "Vaccine",
  "Packaging",
  "Bedding",
  "Sanitation",
  "EquipmentPart",
  "Other",
] as const;
export type InventoryCategoryValue = (typeof INVENTORY_CATEGORY_VALUES)[number];
const INVENTORY_CATEGORY_KEYS = {
  Feed: "enums:inventoryCategory.Feed",
  Supplement: "enums:inventoryCategory.Supplement",
  Additive: "enums:inventoryCategory.Additive",
  Medication: "enums:inventoryCategory.Medication",
  Vaccine: "enums:inventoryCategory.Vaccine",
  Packaging: "enums:inventoryCategory.Packaging",
  Bedding: "enums:inventoryCategory.Bedding",
  Sanitation: "enums:inventoryCategory.Sanitation",
  EquipmentPart: "enums:inventoryCategory.EquipmentPart",
  Other: "enums:inventoryCategory.Other",
} as const satisfies Record<InventoryCategoryValue, EnumsKey>;
export function inventoryCategoryLabel(value: InventoryCategoryValue | (string & {})): string {
  const key = INVENTORY_CATEGORY_KEYS[value as InventoryCategoryValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// inventory movement type (InventoryPage ledger) — InventoryMovementType enum.
// ---------------------------------------------------------------------------
export const INVENTORY_MOVEMENT_VALUES = ["Purchase", "Usage", "Adjustment", "Discard"] as const;
export type InventoryMovementValue = (typeof INVENTORY_MOVEMENT_VALUES)[number];
const INVENTORY_MOVEMENT_KEYS = {
  Purchase: "enums:inventoryMovement.Purchase",
  Usage: "enums:inventoryMovement.Usage",
  Adjustment: "enums:inventoryMovement.Adjustment",
  Discard: "enums:inventoryMovement.Discard",
} as const satisfies Record<InventoryMovementValue, EnumsKey>;
export function inventoryMovementLabel(value: InventoryMovementValue | (string & {})): string {
  const key = INVENTORY_MOVEMENT_KEYS[value as InventoryMovementValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// flock (bird) movement type (FlocksPage ledger) — BirdMovementType enum.
// ---------------------------------------------------------------------------
export const FLOCK_MOVEMENT_VALUES = ["Mortality", "Cull", "Adjustment"] as const;
export type FlockMovementValue = (typeof FLOCK_MOVEMENT_VALUES)[number];
const FLOCK_MOVEMENT_KEYS = {
  Mortality: "enums:flockMovement.Mortality",
  Cull: "enums:flockMovement.Cull",
  Adjustment: "enums:flockMovement.Adjustment",
} as const satisfies Record<FlockMovementValue, EnumsKey>;
export function flockMovementLabel(value: FlockMovementValue | (string & {})): string {
  const key = FLOCK_MOVEMENT_KEYS[value as FlockMovementValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// egg stock movement type (StockPage lot ledger) — EggMovementType enum.
// ---------------------------------------------------------------------------
export const STOCK_MOVEMENT_VALUES = [
  "Production",
  "Sale",
  "Adjustment",
  "Discard",
  "InternalUse",
  "Transfer",
  "Reconciliation",
  "Void",
] as const;
export type StockMovementValue = (typeof STOCK_MOVEMENT_VALUES)[number];
const STOCK_MOVEMENT_KEYS = {
  Production: "enums:stockMovement.Production",
  Sale: "enums:stockMovement.Sale",
  Adjustment: "enums:stockMovement.Adjustment",
  Discard: "enums:stockMovement.Discard",
  InternalUse: "enums:stockMovement.InternalUse",
  Transfer: "enums:stockMovement.Transfer",
  Reconciliation: "enums:stockMovement.Reconciliation",
  Void: "enums:stockMovement.Void",
} as const satisfies Record<StockMovementValue, EnumsKey>;
export function stockMovementLabel(value: StockMovementValue | (string & {})): string {
  const key = STOCK_MOVEMENT_KEYS[value as StockMovementValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// unit system (SettingsPage) — UnitSystem enum.
// ---------------------------------------------------------------------------
export const UNIT_SYSTEM_VALUES = ["Metric", "Imperial"] as const;
export type UnitSystemValue = (typeof UNIT_SYSTEM_VALUES)[number];
const UNIT_SYSTEM_KEYS = {
  Metric: "enums:unitSystem.Metric",
  Imperial: "enums:unitSystem.Imperial",
} as const satisfies Record<UnitSystemValue, EnumsKey>;
export function unitSystemLabel(value: UnitSystemValue | (string & {})): string {
  const key = UNIT_SYSTEM_KEYS[value as UnitSystemValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// worker sale-allocation policy (SettingsPage picker) — #612
// WorkerSaleAllocationPolicy enum.
// ---------------------------------------------------------------------------
export const WORKER_SALE_ALLOCATION_POLICY_VALUES = ["AssignedFlocksOnly", "AllFarmFlocks"] as const;
export type WorkerSaleAllocationPolicyValue = (typeof WORKER_SALE_ALLOCATION_POLICY_VALUES)[number];
const WORKER_SALE_ALLOCATION_POLICY_KEYS = {
  AssignedFlocksOnly: "enums:workerSaleAllocationPolicy.AssignedFlocksOnly",
  AllFarmFlocks: "enums:workerSaleAllocationPolicy.AllFarmFlocks",
} as const satisfies Record<WorkerSaleAllocationPolicyValue, EnumsKey>;
export function workerSaleAllocationPolicyLabel(
  value: WorkerSaleAllocationPolicyValue | (string & {}),
): string {
  const key = WORKER_SALE_ALLOCATION_POLICY_KEYS[value as WorkerSaleAllocationPolicyValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// weekday (SettingsPage week-start picker) — standalone day-name labels.
// ---------------------------------------------------------------------------
export const WEEKDAY_VALUES = [
  "Sunday",
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
] as const;
export type WeekdayValue = (typeof WEEKDAY_VALUES)[number];
const WEEKDAY_KEYS = {
  Sunday: "enums:weekday.Sunday",
  Monday: "enums:weekday.Monday",
  Tuesday: "enums:weekday.Tuesday",
  Wednesday: "enums:weekday.Wednesday",
  Thursday: "enums:weekday.Thursday",
  Friday: "enums:weekday.Friday",
  Saturday: "enums:weekday.Saturday",
} as const satisfies Record<WeekdayValue, EnumsKey>;
export function weekdayLabel(value: WeekdayValue | (string & {})): string {
  const key = WEEKDAY_KEYS[value as WeekdayValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// audit action (AuditPage filter + table) — AuditEvent.action, a server
// "Entity.Verb" capture-point code (#182, Task 29). This is also the single
// source of truth for the filter's option list — AuditPage no longer keeps
// its own local `actions` array (part of the #84 magic-strings debt).
// ---------------------------------------------------------------------------
export const AUDIT_ACTION_VALUES = [
  "DailyEntry.Adjust", "DailyEntry.Void", "SalesOrder.Void", "Payment.Void",
  "Expense.Adjust", "ExpenseCategory.Update", "InventoryItem.Adjust",
  "WaterUsage.Correct", "Flock.BirdMovement", "Flock.Update", "Flock.Deplete",
  "Flock.Archive", "Flock.Reactivate", "EggGrade.Update", "EggGrade.Activate",
  "EggGrade.Deactivate", "User.Create", "User.Update", "User.PasswordSet",
  "User.PasswordChanged", "User.BreakGlassReset", "User.RoleChanged", "User.EmailChanged", "User.Disabled", "User.Enabled",
  "User.FlockAssign", "User.FlockUnassign",
  "Account.Export", "Account.SetLogo", "Account.RemoveLogo",
  "Account.SetBanner", "Account.RemoveBanner", "Account.UpdateSettings",
  "Account.Suspend", "Account.Reactivate", "Account.Provisioned",
  "Product.Create", "Product.Update", "Product.Activate",
  "Product.Deactivate", "EggUnitConversion.Update", "EggLot.Movement",
  "Flock.Create", "DailyEntry.Create", "DailyEntry.Update", "DailyEntry.Submit",
  "SalesOrder.Create", "SalesOrder.Confirm", "SalesOrder.Cancel",
  "SalesOrder.AddItem", "SalesOrder.UpdateItem", "SalesOrder.RemoveItem",
  "Expense.Create", "EggGrade.Create", "Customer.Create", "Customer.Update",
] as const;
export type AuditActionValue = (typeof AUDIT_ACTION_VALUES)[number];
const AUDIT_ACTION_KEYS = {
  "DailyEntry.Adjust": "enums:auditAction.DailyEntry.Adjust",
  "DailyEntry.Void": "enums:auditAction.DailyEntry.Void",
  "SalesOrder.Void": "enums:auditAction.SalesOrder.Void",
  "Payment.Void": "enums:auditAction.Payment.Void",
  "Expense.Adjust": "enums:auditAction.Expense.Adjust",
  "ExpenseCategory.Update": "enums:auditAction.ExpenseCategory.Update",
  "InventoryItem.Adjust": "enums:auditAction.InventoryItem.Adjust",
  "WaterUsage.Correct": "enums:auditAction.WaterUsage.Correct",
  "Flock.BirdMovement": "enums:auditAction.Flock.BirdMovement",
  "Flock.Update": "enums:auditAction.Flock.Update",
  "Flock.Deplete": "enums:auditAction.Flock.Deplete",
  "Flock.Archive": "enums:auditAction.Flock.Archive",
  "Flock.Reactivate": "enums:auditAction.Flock.Reactivate",
  "EggGrade.Update": "enums:auditAction.EggGrade.Update",
  "EggGrade.Activate": "enums:auditAction.EggGrade.Activate",
  "EggGrade.Deactivate": "enums:auditAction.EggGrade.Deactivate",
  "User.Create": "enums:auditAction.User.Create",
  "User.Update": "enums:auditAction.User.Update",
  "User.PasswordSet": "enums:auditAction.User.PasswordSet",
  "User.PasswordChanged": "enums:auditAction.User.PasswordChanged",
  "User.BreakGlassReset": "enums:auditAction.User.BreakGlassReset",
  "User.RoleChanged": "enums:auditAction.User.RoleChanged",
  "User.EmailChanged": "enums:auditAction.User.EmailChanged",
  "User.Disabled": "enums:auditAction.User.Disabled",
  "User.Enabled": "enums:auditAction.User.Enabled",
  "User.FlockAssign": "enums:auditAction.User.FlockAssign",
  "User.FlockUnassign": "enums:auditAction.User.FlockUnassign",
  "Account.Export": "enums:auditAction.Account.Export",
  "Account.SetLogo": "enums:auditAction.Account.SetLogo",
  "Account.RemoveLogo": "enums:auditAction.Account.RemoveLogo",
  "Account.SetBanner": "enums:auditAction.Account.SetBanner",
  "Account.RemoveBanner": "enums:auditAction.Account.RemoveBanner",
  "Account.UpdateSettings": "enums:auditAction.Account.UpdateSettings",
  "Account.Suspend": "enums:auditAction.Account.Suspend",
  "Account.Reactivate": "enums:auditAction.Account.Reactivate",
  "Account.Provisioned": "enums:auditAction.Account.Provisioned",
  "Product.Create": "enums:auditAction.Product.Create",
  "Product.Update": "enums:auditAction.Product.Update",
  "Product.Activate": "enums:auditAction.Product.Activate",
  "Product.Deactivate": "enums:auditAction.Product.Deactivate",
  "EggUnitConversion.Update": "enums:auditAction.EggUnitConversion.Update",
  "EggLot.Movement": "enums:auditAction.EggLot.Movement",
  "Flock.Create": "enums:auditAction.Flock.Create",
  "DailyEntry.Create": "enums:auditAction.DailyEntry.Create",
  "DailyEntry.Update": "enums:auditAction.DailyEntry.Update",
  "DailyEntry.Submit": "enums:auditAction.DailyEntry.Submit",
  "SalesOrder.AddItem": "enums:auditAction.SalesOrder.AddItem",
  "SalesOrder.UpdateItem": "enums:auditAction.SalesOrder.UpdateItem",
  "SalesOrder.RemoveItem": "enums:auditAction.SalesOrder.RemoveItem",
  "SalesOrder.Create": "enums:auditAction.SalesOrder.Create",
  "SalesOrder.Confirm": "enums:auditAction.SalesOrder.Confirm",
  "SalesOrder.Cancel": "enums:auditAction.SalesOrder.Cancel",
  "Expense.Create": "enums:auditAction.Expense.Create",
  "EggGrade.Create": "enums:auditAction.EggGrade.Create",
  "Customer.Create": "enums:auditAction.Customer.Create",
  "Customer.Update": "enums:auditAction.Customer.Update",
} as const satisfies Record<AuditActionValue, EnumsKey>;
export function auditActionLabel(value: AuditActionValue | (string & {})): string {
  const key = AUDIT_ACTION_KEYS[value as AuditActionValue];
  return key ? i18n.t(key) : String(value);
}

// Which EntityType each action is actually recorded against server-side —
// read off every audit.WriteAsync(...) call site, not derived from the
// action's own "Entity.Verb" prefix, which is misleading for the four
// Account.Set/RemoveLogo/Banner actions (prefixed "Account", recorded
// against "FarmLogo" — a shared row FarmLogo.cs documents as covering both
// logo and banner). Powers AuditPage's entity-type dropdown, which narrows
// the action dropdown to only actions that can occur on the selected type.
export const AUDIT_ACTION_ENTITY_TYPE = {
  "DailyEntry.Adjust": "DailyEntry",
  "DailyEntry.Void": "DailyEntry",
  "SalesOrder.Void": "SalesOrder",
  "Payment.Void": "Payment",
  "Expense.Adjust": "Expense",
  "ExpenseCategory.Update": "ExpenseCategory",
  "InventoryItem.Adjust": "InventoryItem",
  "WaterUsage.Correct": "WaterUsage",
  "Flock.BirdMovement": "Flock",
  "Flock.Update": "Flock",
  "Flock.Deplete": "Flock",
  "Flock.Archive": "Flock",
  "Flock.Reactivate": "Flock",
  "EggGrade.Update": "EggGrade",
  "EggGrade.Activate": "EggGrade",
  "EggGrade.Deactivate": "EggGrade",
  "User.Create": "User",
  "User.Update": "User",
  "User.PasswordSet": "User",
  "User.PasswordChanged": "User",
  "User.BreakGlassReset": "User",
  "User.RoleChanged": "User",
  "User.EmailChanged": "User",
  "User.Disabled": "User",
  "User.Enabled": "User",
  "User.FlockAssign": "User",
  "User.FlockUnassign": "User",
  "Account.Export": "Account",
  "Account.SetLogo": "FarmLogo",
  "Account.RemoveLogo": "FarmLogo",
  "Account.SetBanner": "FarmLogo",
  "Account.RemoveBanner": "FarmLogo",
  "Account.UpdateSettings": "Account",
  "Account.Suspend": "Account",
  "Account.Reactivate": "Account",
  "Account.Provisioned": "Account",
  "Product.Create": "Product",
  "Product.Update": "Product",
  "Product.Activate": "Product",
  "Product.Deactivate": "Product",
  "EggUnitConversion.Update": "EggUnitConversion",
  "EggLot.Movement": "EggLot",
  "Flock.Create": "Flock",
  "DailyEntry.Create": "DailyEntry",
  "DailyEntry.Update": "DailyEntry",
  "DailyEntry.Submit": "DailyEntry",
  "SalesOrder.Create": "SalesOrder",
  "SalesOrder.Confirm": "SalesOrder",
  "SalesOrder.Cancel": "SalesOrder",
  "SalesOrder.AddItem": "SalesOrder",
  "SalesOrder.UpdateItem": "SalesOrder",
  "SalesOrder.RemoveItem": "SalesOrder",
  "Expense.Create": "Expense",
  "EggGrade.Create": "EggGrade",
  "Customer.Create": "Customer",
  "Customer.Update": "Customer",
} as const satisfies Record<AuditActionValue, EntityTypeValue>;

// ---------------------------------------------------------------------------
// entity type (AuditPage table entity cell) — AuditEvent.entityType.
// ---------------------------------------------------------------------------
export const ENTITY_TYPE_VALUES = [
  "Account", "Customer", "DailyEntry", "EggGrade", "EggLot", "EggUnitConversion", "Expense",
  "ExpenseCategory", "FarmLogo", "Flock", "InventoryItem", "Payment", "Product",
  "SalesOrder", "User", "WaterUsage",
] as const;
export type EntityTypeValue = (typeof ENTITY_TYPE_VALUES)[number];
const ENTITY_TYPE_KEYS = {
  Account: "enums:entityType.Account",
  Customer: "enums:entityType.Customer",
  DailyEntry: "enums:entityType.DailyEntry",
  EggGrade: "enums:entityType.EggGrade",
  EggLot: "enums:entityType.EggLot",
  EggUnitConversion: "enums:entityType.EggUnitConversion",
  Expense: "enums:entityType.Expense",
  ExpenseCategory: "enums:entityType.ExpenseCategory",
  FarmLogo: "enums:entityType.FarmLogo",
  Flock: "enums:entityType.Flock",
  InventoryItem: "enums:entityType.InventoryItem",
  Payment: "enums:entityType.Payment",
  Product: "enums:entityType.Product",
  SalesOrder: "enums:entityType.SalesOrder",
  User: "enums:entityType.User",
  WaterUsage: "enums:entityType.WaterUsage",
} as const satisfies Record<EntityTypeValue, EnumsKey>;
export function entityTypeLabel(value: EntityTypeValue | (string & {})): string {
  const key = ENTITY_TYPE_KEYS[value as EntityTypeValue];
  return key ? i18n.t(key) : String(value);
}

// Machine-iterable registry of every family: its raw-value tuple, its key map,
// and its label function. enums.test.ts walks this to assert each value resolves
// to a real, non-empty en.enums entry — so a new family added above is covered
// by the test the moment it is registered here, with nothing else to edit.
export const ENUMS = {
  status: { values: STATUS_VALUES, keys: STATUS_KEYS, label: statusLabel },
  role: { values: ROLE_VALUES, keys: ROLE_KEYS, label: roleLabel },
  waterSource: { values: WATER_SOURCE_VALUES, keys: WATER_SOURCE_KEYS, label: waterSourceLabel },
  waterUnit: { values: WATER_UNIT_VALUES, keys: WATER_UNIT_KEYS, label: waterUnitLabel },
  gradeType: { values: GRADE_TYPE_VALUES, keys: GRADE_TYPE_KEYS, label: gradeTypeLabel },
  inventoryCategory: {
    values: INVENTORY_CATEGORY_VALUES,
    keys: INVENTORY_CATEGORY_KEYS,
    label: inventoryCategoryLabel,
  },
  inventoryMovement: {
    values: INVENTORY_MOVEMENT_VALUES,
    keys: INVENTORY_MOVEMENT_KEYS,
    label: inventoryMovementLabel,
  },
  flockMovement: {
    values: FLOCK_MOVEMENT_VALUES,
    keys: FLOCK_MOVEMENT_KEYS,
    label: flockMovementLabel,
  },
  stockMovement: {
    values: STOCK_MOVEMENT_VALUES,
    keys: STOCK_MOVEMENT_KEYS,
    label: stockMovementLabel,
  },
  unitSystem: { values: UNIT_SYSTEM_VALUES, keys: UNIT_SYSTEM_KEYS, label: unitSystemLabel },
  workerSaleAllocationPolicy: {
    values: WORKER_SALE_ALLOCATION_POLICY_VALUES,
    keys: WORKER_SALE_ALLOCATION_POLICY_KEYS,
    label: workerSaleAllocationPolicyLabel,
  },
  weekday: { values: WEEKDAY_VALUES, keys: WEEKDAY_KEYS, label: weekdayLabel },
  auditAction: { values: AUDIT_ACTION_VALUES, keys: AUDIT_ACTION_KEYS, label: auditActionLabel },
  entityType: { values: ENTITY_TYPE_VALUES, keys: ENTITY_TYPE_KEYS, label: entityTypeLabel },
} as const;
