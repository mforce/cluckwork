// Farm operations label families: water, grades, inventory and the movement ledgers.
// See the module header in ./enums.ts for the compile-time scheme
// (VALUES tuple -> KEYS map -> label function) and ./enums-key.ts for the
// key-constraint type.
import i18n from "./index";
import type { EnumsKey } from "./enums-key";

// ---------------------------------------------------------------------------
// water source (WaterPage) — WaterSource enum.
// ---------------------------------------------------------------------------
export const WATER_SOURCE_VALUES = ["Well", "Municipal", "Tank", "Other"] as const;
export type WaterSourceValue = (typeof WATER_SOURCE_VALUES)[number];
export const WATER_SOURCE_KEYS = {
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
export const WATER_UNIT_KEYS = {
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
export const GRADE_TYPE_KEYS = {
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
export const INVENTORY_CATEGORY_KEYS = {
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
export const INVENTORY_MOVEMENT_KEYS = {
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
export const FLOCK_MOVEMENT_KEYS = {
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
export const STOCK_MOVEMENT_KEYS = {
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
