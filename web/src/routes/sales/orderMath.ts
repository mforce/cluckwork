import type { OrderItem, Product, SalesOrder } from "../../api/cluckwork";
import i18n from "../../i18n";

// The egg selling units, in picker order — one list for the Per picker and
// the #445 unit-label/preview helpers, mirroring the server's ProductUnit
// egg subset. `as const` keeps `unit${SellingUnit}` a closed union the typed
// i18n `t` accepts (a bare string template fails the tsc -b build).
export const SELLING_UNITS = ["Egg", "Dozen", "Flat", "Tray", "Carton", "Case"] as const;
type SellingUnit = (typeof SELLING_UNITS)[number];
export const isSellingUnit = (u: string): u is SellingUnit =>
  (SELLING_UNITS as readonly string[]).includes(u);

// The sole RAW payment-method render site (usePayments.tsx) mirrors the SAME six-value
// vocabulary as the payment-method picker there (which already renders
// via the translated sales:method* keys) rather than the English-only `enums`
// module — method was deliberately left out of enums in Task 4 because it
// already carries es/tl translations in the `sales` namespace (#182).
export type PaymentMethod = "Cash" | "Check" | "Card" | "BankTransfer" | "MobilePayment" | "Other";

// #512 US5 (T057, FR-046/048) — the canonical 8-4-4-4-12 GUID shape,
// case-insensitive on input, normalized to lowercase on output. A malformed
// value (wrong shape, extra text, empty) is treated as absent — never sent to
// the server and never used to name a filter — per FR-048; the URL itself is
// left as the user typed it (not rewritten merely to clean it up).
const CANONICAL_GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function normalizeCanonicalGuid(raw: string | null): string {
  if (raw === null || !CANONICAL_GUID_RE.test(raw)) return "";
  return raw.toLowerCase();
}

// A stored price as the decimal string the price field holds, at `scale` minor
// units. Blank when the product carries no default, and blank rather than a
// guess when the scale is not known yet — a wrong scale here is a price out by
// a factor of a hundred, and an empty field simply falls back to the server's
// own default for the line.
export function priceInput(defaultPriceMinorUnits: number | null, scale: number | null): string {
  if (defaultPriceMinorUnits === null || scale === null) return "";
  return (defaultPriceMinorUnits / 10 ** scale).toFixed(scale);
}

// A live line editor retains both raw inputs until an observed conflict is reloaded.
export type EditorDraft = {
  orderId: string;
  itemId: string;
  quantity: number;
  price: string;
  seedQuantity: number;
  seedPrice: string;
  serverQuantity: number;
  serverPrice: number;
};

export function lineDraft(order: SalesOrder, item: OrderItem): EditorDraft {
  const price = priceInput(item.unitPriceMinorUnits, order.currencyMinorUnit);
  return {
    orderId: order.id, itemId: item.id,
    quantity: item.quantity, price,
    seedQuantity: item.quantity, seedPrice: price,
    serverQuantity: item.quantity, serverPrice: item.unitPriceMinorUnits,
  };
}

export function editableLine(order: SalesOrder | null, draft: EditorDraft | null): OrderItem | undefined {
  if (!order || !draft || order.status !== "Draft" || order.id !== draft.orderId) return;
  return order.items.find((item) => item.id === draft.itemId);
}

export function lineChanged(item: OrderItem, draft: EditorDraft): boolean {
  return item.quantity !== draft.serverQuantity || item.unitPriceMinorUnits !== draft.serverPrice;
}

// #720 — extracted from the initial load so the post-rejection refresh applies
// the SAME filter. Two copies of this rule would drift, and the screen would
// start offering a product that Add line then 422s (codex review of #100).
export function sellableProducts(products: Product[], grades: { id: string; isSaleable: boolean }[]): Product[] {
  const saleable = new Set(grades.filter((x) => x.isSaleable).map((x) => x.id));
  return products.filter((x) => x.active && x.eggGradeId !== null && saleable.has(x.eggGradeId));
}

// #720 — the discount a line gave away, and how it renders.
//
// Branch on the comparison DIRECTLY. There is no max(list - unit, 0) clamp:
// the only branch that reads the difference is the below-list one, where it is
// positive by construction, so a clamp there could never fire — and a guard
// that cannot fire reads as safety without being any.
//
// The percent multiplier is 100, not 1000. fmt.count(value, fractionDigits?)
// (useFormat.ts:17 — locale is already bound) renders the value AS GIVEN with
// one fraction digit, so a x1000 scale would print
// 111.1% where 11.1% is meant. Intl.NumberFormat's default roundingMode is
// halfExpand — half-up for positives — which is the rounding wanted here, so
// no rounding scaffolding is needed.
type LineDiscount =
  | { kind: "none" }            // no comparable list price
  | { kind: "atList" }
  | { kind: "below"; amountMinorUnits: number; percent: number }
  | { kind: "above" };

export function lineDiscount(item: OrderItem): LineDiscount {
  const list = item.listUnitPriceMinorUnits;
  if (list === null) return { kind: "none" };
  if (item.unitPriceMinorUnits > list) return { kind: "above" };
  if (item.unitPriceMinorUnits === list) return { kind: "atList" };
  const perUnit = list - item.unitPriceMinorUnits;
  return {
    kind: "below",
    amountMinorUnits: perUnit * item.quantity,
    percent: (perUnit * 100) / list,
  };
}

// #723/#724 — the ORDER's discount, aggregated from its lines.
//
// "Comparable" means listUnitPriceMinorUnits !== null. An at-list line and an
// ABOVE-list line are both comparable and both belong in the denominator: the
// figure answers "how much of this order's list value was given away", and a
// line sold over list still contributed list value. An earlier draft excluded
// above-list lines and overstated a mixed order — one $100-at-$110 line beside
// one $100-at-$90 line reported 10% where 5% is the truth.
//
// `partial` sits on BOTH populated variants, not only on "below". An order of
// one no-list-price line plus one at-list line otherwise has no representable
// state and reads as a measured zero, which is exactly the "a discount hides
// behind an unpriced product" failure #719 exists to stop.
//
// A ZERO list price is legal — Product.cs:38 and :66 reject only negatives — so
// listValueMinorUnits can be 0 while comparable lines exist. But reaching the
// division with a zero denominator ALSO needs a below-list line against that
// zero list, which needs a NEGATIVE unit price, and
// UpdateOrderItemValidator.cs:11 requires UnitPriceMinorUnits >= 0.
//
// So this guard cannot fire against data the API will persist. It is kept
// anyway, for one reason stated plainly rather than dressed up as safety: this
// function consumes an API RESPONSE in a display path, and what it prevents is
// rendering "∞%" to a user. It is not a clamp masking a wrong value — the null
// selects a different, already-translated string, the same shape #720 ships as
// listPriceHintBelowNoPct.
export type OrderDiscount =
  | { kind: "unknown" }
  | { kind: "atList"; partial: boolean }
  | { kind: "below"; amountMinorUnits: number; percent: number | null; partial: boolean };

export function orderDiscount(items: OrderItem[]): OrderDiscount {
  // An order with no lines has nothing to be unknown ABOUT: "unknown" is
  // reserved for an order whose lines exist but predate the snapshot.
  if (items.length === 0) return { kind: "atList", partial: false };

  let amountMinorUnits = 0;
  let listValueMinorUnits = 0;
  let comparable = 0;
  let partial = false;

  for (const item of items) {
    const line = lineDiscount(item);
    if (line.kind === "none") {
      partial = true;
      continue;
    }
    comparable += 1;
    // Non-null on every branch lineDiscount reaches past "none"; the ?? 0 is a
    // type narrowing, not a fallback, and can never supply a value.
    listValueMinorUnits += (item.listUnitPriceMinorUnits ?? 0) * item.quantity;
    if (line.kind === "below") amountMinorUnits += line.amountMinorUnits;
  }

  if (comparable === 0) return { kind: "unknown" };
  if (amountMinorUnits === 0) return { kind: "atList", partial };
  return {
    kind: "below",
    amountMinorUnits,
    percent: listValueMinorUnits > 0 ? (amountMinorUnits * 100) / listValueMinorUnits : null,
    partial,
  };
}

// #773 — which of the two "no list price" answers an ORDER reports. PreDating
// only when EVERY line predates capture: one line that genuinely had no
// comparable price makes "this order predates list-price capture" false of the
// order as a whole. The other answer is named by ProductUnpriced, which shares
// its label with NotComparable (LIST_PRICE_BASIS_KEYS collapses the two), so
// the representative chosen here is a labelling detail, not a claim about the
// lines.
//
// `[].every()` is TRUE, so an empty order would answer "PreDating" here. It
// cannot: both call sites sit behind orderDiscount(...).kind === "unknown",
// and orderDiscount bails to `atList` for an empty order before measuring, so
// an order with nothing to measure is never called unmeasurable. Verified by
// mutation rather than by reading: delete that bail and "an order with no
// lines reads as an em dash" goes red.
type OrderListPriceBasis = "allPreDating" | "nonePreDating" | "mixed";

export function orderListPriceBasis(items: OrderItem[]): OrderListPriceBasis {
  const preDating = items.filter((i) => i.listPriceBasis === "PreDating").length;
  if (preDating === 0) return "nonePreDating";
  return preDating === items.length ? "allPreDating" : "mixed";
}

export function orderIsAtList(items: OrderItem[]): boolean {
  return items.length > 0 && items.every((item) => item.unitPriceMinorUnits === item.listUnitPriceMinorUnits);
}

export function orderListValue(items: OrderItem[]): number | null {
  let total = 0;
  for (const item of items) {
    if (item.listUnitPriceMinorUnits === null) return null;
    total += item.listUnitPriceMinorUnits * item.quantity;
  }
  return total;
}

// Exact decimal parsing in the ORDER's denomination (no float multiply —
// #88 review); excess decimals are rejected, not silently rounded.
export function toMinor(display: string, minor: number) {
  const m = display.trim().match(/^(\d+)(?:\.(\d+))?$/);
  if (!m) throw new Error(i18n.t("sales:enterValidAmount"));
  const frac = m[2] ?? "";
  if (frac.length > minor)
    throw new Error(minor === 0
      ? i18n.t("sales:noDecimalPlaces")
      : i18n.t("sales:atMostDecimals", { count: minor }));
  const v = Number(m[1]) * 10 ** minor + Number(frac.padEnd(minor, "0") || "0");
  if (!Number.isSafeInteger(v) || v <= 0) throw new Error(i18n.t("sales:enterAmountGreaterThanZero"));
  return v;
}
