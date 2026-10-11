// Farm configuration and people: roles (UsersPage) and the SettingsPage pickers.
// See the module header in ./enums.ts for the compile-time scheme
// (VALUES tuple -> KEYS map -> label function) and ./enums-key.ts for the
// key-constraint type.
import i18n from "./index";
import type { EnumsKey } from "./enums-key";

// ---------------------------------------------------------------------------
// role (UsersPage) — Roles.Assignable plus Worker (a user with no role row).
// ---------------------------------------------------------------------------
export const ROLE_VALUES = ["Worker", "Admin", "Manager", "Sales", "ReadOnly"] as const;
export type RoleValue = (typeof ROLE_VALUES)[number];
export const ROLE_KEYS = {
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
// unit system (SettingsPage) — UnitSystem enum.
// ---------------------------------------------------------------------------
export const UNIT_SYSTEM_VALUES = ["Metric", "Imperial"] as const;
export type UnitSystemValue = (typeof UNIT_SYSTEM_VALUES)[number];
export const UNIT_SYSTEM_KEYS = {
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
export const WORKER_SALE_ALLOCATION_POLICY_KEYS = {
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
export const WEEKDAY_KEYS = {
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
