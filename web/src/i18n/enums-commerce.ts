// Commerce (Sales) label families.
// See the module header in ./enums.ts for the compile-time scheme
// (VALUES tuple -> KEYS map -> label function) and ./enums-key.ts for the
// key-constraint type.
import i18n from "./index";
import type { EnumsKey } from "./enums-key";

// NOTE: payment method and sale unit are deliberately NOT here. They already
// live in the TRANSLATED `sales` namespace (sales:method*, sales:unit*), which
// carries its own es/tl. `enums` is in TRANSLATED_NAMESPACES too (#182,
// pending native review), so adding them here would just be a redundant
// second copy of the same vocabulary, not a translation-coverage gain. Sale
// unit's only other render site (ProductsPage `{p.defaultUnit}`) shows the
// raw value today — nothing is lost by leaving it until a native pass
// decides on a shared source.

// ---------------------------------------------------------------------------
// discount reason (SalesPage) — DiscountReasonCode (#721). The tuple's ORDER is
// the picklist's order on screen, most routine first; the wire values are the
// server enum's member names and are never rendered raw.
// ---------------------------------------------------------------------------
export const DISCOUNT_REASON_VALUES = [
  "Volume", "DamagedStock", "LongStandingCustomer", "ManagerApproved", "Other",
] as const;
export type DiscountReasonValue = (typeof DISCOUNT_REASON_VALUES)[number];
export const DISCOUNT_REASON_KEYS = {
  Volume: "enums:discountReason.Volume",
  DamagedStock: "enums:discountReason.DamagedStock",
  LongStandingCustomer: "enums:discountReason.LongStandingCustomer",
  ManagerApproved: "enums:discountReason.ManagerApproved",
  Other: "enums:discountReason.Other",
} as const satisfies Record<DiscountReasonValue, EnumsKey>;
export function discountReasonLabel(value: DiscountReasonValue | (string & {})): string {
  const key = DISCOUNT_REASON_KEYS[value as DiscountReasonValue];
  return key ? i18n.t(key) : String(value);
}

// ---------------------------------------------------------------------------
// list price basis (SalesPage) — ListPriceBasis (#773). WHY a line's list
// price is what it is, which a bare null could not say.
//
// TWO labels for four values, and the collapse lives in the map on purpose:
// NotComparable shares ProductUnpriced's key because "the product had no
// price" and "the denominations did not match" are both the recorded fact NO
// COMPARABLE LIST PRICE, while only PreDating is missing data. Recorded never
// reaches a screen today — a recorded line renders the price itself — but the
// map stays total, which is this module's own contract.
// ---------------------------------------------------------------------------
export const LIST_PRICE_BASIS_VALUES = [
  "Recorded", "ProductUnpriced", "NotComparable", "PreDating",
] as const;
export type ListPriceBasisValue = (typeof LIST_PRICE_BASIS_VALUES)[number];
export const LIST_PRICE_BASIS_KEYS = {
  Recorded: "enums:listPriceBasis.Recorded",
  ProductUnpriced: "enums:listPriceBasis.ProductUnpriced",
  NotComparable: "enums:listPriceBasis.ProductUnpriced",
  PreDating: "enums:listPriceBasis.PreDating",
} as const satisfies Record<ListPriceBasisValue, EnumsKey>;
export function listPriceBasisLabel(value: ListPriceBasisValue | (string & {})): string {
  const key = LIST_PRICE_BASIS_KEYS[value as ListPriceBasisValue];
  return key ? i18n.t(key) : String(value);
}
