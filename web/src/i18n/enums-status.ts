// Shared status pill labels, sourced from StatusBadge.STATUS_VALUES.
// See the module header in ./enums.ts for the compile-time scheme
// (VALUES tuple -> KEYS map -> label function) and ./enums-key.ts for the
// key-constraint type.
import { STATUS_VALUES } from "../components/StatusBadge";
import i18n from "./index";
import type { EnumsKey } from "./enums-key";

// ---------------------------------------------------------------------------
// status — sourced from StatusBadge.STATUS_VALUES so the pill vocabulary and its
// labels are one thing. Identity labels except ManagerAdjusted -> "Adjusted".
// ---------------------------------------------------------------------------
export type StatusValue = (typeof STATUS_VALUES)[number];
export const STATUS_KEYS = {
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
