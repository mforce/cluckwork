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
// `enums` is intentionally NOT in TRANSLATED_NAMESPACES: it is English-only for
// now and es/tl fall back to en for it until a native enum-translation pass.
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
export function statusLabel(value: StatusValue): string {
  return i18n.t(STATUS_KEYS[value]);
}

// ---------------------------------------------------------------------------
// payment method (SalesPage) — PaymentMethod enum.
// ---------------------------------------------------------------------------
export const PAYMENT_METHOD_VALUES = [
  "Cash",
  "Check",
  "Card",
  "BankTransfer",
  "MobilePayment",
  "Other",
] as const;
export type PaymentMethodValue = (typeof PAYMENT_METHOD_VALUES)[number];
const PAYMENT_METHOD_KEYS = {
  Cash: "enums:method.Cash",
  Check: "enums:method.Check",
  Card: "enums:method.Card",
  BankTransfer: "enums:method.BankTransfer",
  MobilePayment: "enums:method.MobilePayment",
  Other: "enums:method.Other",
} as const satisfies Record<PaymentMethodValue, EnumsKey>;
export function paymentMethodLabel(value: PaymentMethodValue): string {
  return i18n.t(PAYMENT_METHOD_KEYS[value]);
}

// ---------------------------------------------------------------------------
// sale unit (SalesPage line/unit picker) — the ProductUnit subset the sale UI
// offers. NOT product unitCode, which is free-form data.
// ---------------------------------------------------------------------------
export const SALE_UNIT_VALUES = ["Egg", "Dozen", "Flat", "Tray", "Carton", "Case"] as const;
export type SaleUnitValue = (typeof SALE_UNIT_VALUES)[number];
const SALE_UNIT_KEYS = {
  Egg: "enums:saleUnit.Egg",
  Dozen: "enums:saleUnit.Dozen",
  Flat: "enums:saleUnit.Flat",
  Tray: "enums:saleUnit.Tray",
  Carton: "enums:saleUnit.Carton",
  Case: "enums:saleUnit.Case",
} as const satisfies Record<SaleUnitValue, EnumsKey>;
export function saleUnitLabel(value: SaleUnitValue): string {
  return i18n.t(SALE_UNIT_KEYS[value]);
}

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
export function roleLabel(value: RoleValue): string {
  return i18n.t(ROLE_KEYS[value]);
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
export function waterSourceLabel(value: WaterSourceValue): string {
  return i18n.t(WATER_SOURCE_KEYS[value]);
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
export function waterUnitLabel(value: WaterUnitValue): string {
  return i18n.t(WATER_UNIT_KEYS[value]);
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
export function gradeTypeLabel(value: GradeTypeValue): string {
  return i18n.t(GRADE_TYPE_KEYS[value]);
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
export function inventoryCategoryLabel(value: InventoryCategoryValue): string {
  return i18n.t(INVENTORY_CATEGORY_KEYS[value]);
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
export function inventoryMovementLabel(value: InventoryMovementValue): string {
  return i18n.t(INVENTORY_MOVEMENT_KEYS[value]);
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
export function flockMovementLabel(value: FlockMovementValue): string {
  return i18n.t(FLOCK_MOVEMENT_KEYS[value]);
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
export function stockMovementLabel(value: StockMovementValue): string {
  return i18n.t(STOCK_MOVEMENT_KEYS[value]);
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
export function unitSystemLabel(value: UnitSystemValue): string {
  return i18n.t(UNIT_SYSTEM_KEYS[value]);
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
export function weekdayLabel(value: WeekdayValue): string {
  return i18n.t(WEEKDAY_KEYS[value]);
}

// Machine-iterable registry of every family: its raw-value tuple, its key map,
// and its label function. enums.test.ts walks this to assert each value resolves
// to a real, non-empty en.enums entry — so a new family added above is covered
// by the test the moment it is registered here, with nothing else to edit.
export const ENUMS = {
  status: { values: STATUS_VALUES, keys: STATUS_KEYS, label: statusLabel },
  method: { values: PAYMENT_METHOD_VALUES, keys: PAYMENT_METHOD_KEYS, label: paymentMethodLabel },
  saleUnit: { values: SALE_UNIT_VALUES, keys: SALE_UNIT_KEYS, label: saleUnitLabel },
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
  weekday: { values: WEEKDAY_VALUES, keys: WEEKDAY_KEYS, label: weekdayLabel },
} as const;
