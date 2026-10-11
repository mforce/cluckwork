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
//
// The families live in sibling modules grouped by domain area — enums-status,
// enums-settings, enums-commerce, enums-operations, enums-audit — sharing the
// key-constraint type in ./enums-key. This file re-exports every family so
// callers keep importing labels from "./enums", and owns the machine-iterable
// ENUMS registry that enums.test.ts walks.
export * from "./enums-status";
export * from "./enums-settings";
export * from "./enums-commerce";
export * from "./enums-operations";
export * from "./enums-audit";

import { STATUS_VALUES } from "../components/StatusBadge";
import {
  STATUS_KEYS,
  statusLabel,
} from "./enums-status";
import {
  ROLE_KEYS,
  ROLE_VALUES,
  roleLabel,
  UNIT_SYSTEM_KEYS,
  UNIT_SYSTEM_VALUES,
  unitSystemLabel,
  WORKER_SALE_ALLOCATION_POLICY_KEYS,
  WORKER_SALE_ALLOCATION_POLICY_VALUES,
  workerSaleAllocationPolicyLabel,
  WEEKDAY_KEYS,
  WEEKDAY_VALUES,
  weekdayLabel,
} from "./enums-settings";
import {
  DISCOUNT_REASON_KEYS,
  DISCOUNT_REASON_VALUES,
  discountReasonLabel,
  LIST_PRICE_BASIS_KEYS,
  LIST_PRICE_BASIS_VALUES,
  listPriceBasisLabel,
} from "./enums-commerce";
import {
  GRADE_TYPE_KEYS,
  GRADE_TYPE_VALUES,
  gradeTypeLabel,
  INVENTORY_CATEGORY_KEYS,
  INVENTORY_CATEGORY_VALUES,
  inventoryCategoryLabel,
  INVENTORY_MOVEMENT_KEYS,
  INVENTORY_MOVEMENT_VALUES,
  inventoryMovementLabel,
  FLOCK_MOVEMENT_KEYS,
  FLOCK_MOVEMENT_VALUES,
  flockMovementLabel,
  STOCK_MOVEMENT_KEYS,
  STOCK_MOVEMENT_VALUES,
  stockMovementLabel,
  WATER_SOURCE_KEYS,
  WATER_SOURCE_VALUES,
  waterSourceLabel,
  WATER_UNIT_KEYS,
  WATER_UNIT_VALUES,
  waterUnitLabel,
} from "./enums-operations";
import {
  AUDIT_ACTION_KEYS,
  AUDIT_ACTION_VALUES,
  auditActionLabel,
  ENTITY_TYPE_KEYS,
  ENTITY_TYPE_VALUES,
  entityTypeLabel,
} from "./enums-audit";

// Machine-iterable registry of every family: its raw-value tuple, its key map,
// and its label function. enums.test.ts walks this to assert each value resolves
// to a real, non-empty en.enums entry — so a new family added in a sibling is covered
// by the test the moment it is registered here, with nothing else to edit.
export const ENUMS = {
  status: { values: STATUS_VALUES, keys: STATUS_KEYS, label: statusLabel },
  role: { values: ROLE_VALUES, keys: ROLE_KEYS, label: roleLabel },
  waterSource: { values: WATER_SOURCE_VALUES, keys: WATER_SOURCE_KEYS, label: waterSourceLabel },
  waterUnit: { values: WATER_UNIT_VALUES, keys: WATER_UNIT_KEYS, label: waterUnitLabel },
  discountReason: {
    values: DISCOUNT_REASON_VALUES,
    keys: DISCOUNT_REASON_KEYS,
    label: discountReasonLabel,
  },
  listPriceBasis: {
    values: LIST_PRICE_BASIS_VALUES,
    keys: LIST_PRICE_BASIS_KEYS,
    label: listPriceBasisLabel,
  },
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
