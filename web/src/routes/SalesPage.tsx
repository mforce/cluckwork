import { useCallback, useEffect, useId, useRef, useState } from "react";
import { FilterX, Plus, ShoppingCart } from "lucide-react";
import { Trans, useTranslation } from "react-i18next";
import { Link, useSearchParams } from "react-router";
import {
  addOrderItem, cancelOrder, confirmOrder, createOrder, getOrder,
  listCustomers, listEggGrades, listEggUnitConversions, listOrderPayments, listOrders,
  listProducts, parseMoneyToMinorUnits, recordPayment,
  removeOrderItem, updateOrderItem, voidOrder, voidPayment,
} from "../api/cluckwork";
import type { Customer, EggUnitConversion, OrderItem, OrderPayments, Product, SalesOrder } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { CustomerPicker } from "../components/CustomerPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { NumberField } from "../components/NumberField";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { EmptyState } from "../components/EmptyState";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { useDialogAction } from "../components/useDialogAction";
import { useConfirm } from "../components/useConfirm";
import { usePagedList } from "../components/usePagedList";
import { StatusBadge } from "../components/StatusBadge";
import { GlossaryLink } from "../components/GlossaryLink";
import { newId } from "../lib/ids";
import { useFarm, useFarmToday } from "../farm/useFarm";
import i18n from "../i18n";
import { DISCOUNT_REASON_VALUES, discountReasonLabel, statusLabel } from "../i18n/enums";

const PAGE = 50;

// The egg selling units, in picker order — one list for the Per picker and
// the #445 unit-label/preview helpers, mirroring the server's ProductUnit
// egg subset. `as const` keeps `unit${SellingUnit}` a closed union the typed
// i18n `t` accepts (a bare string template fails the tsc -b build).
const SELLING_UNITS = ["Egg", "Dozen", "Flat", "Tray", "Carton", "Case"] as const;
type SellingUnit = (typeof SELLING_UNITS)[number];
const isSellingUnit = (u: string): u is SellingUnit =>
  (SELLING_UNITS as readonly string[]).includes(u);

// The sole RAW payment-method render site (below) mirrors the SAME six-value
// vocabulary as the payment-method picker in this file (which already renders
// via the translated sales:method* keys) rather than the English-only `enums`
// module — method was deliberately left out of enums in Task 4 because it
// already carries es/tl translations in the `sales` namespace (#182).
type PaymentMethod = "Cash" | "Check" | "Card" | "BankTransfer" | "MobilePayment" | "Other";

// #512 US5 (T057, FR-046/048) — the canonical 8-4-4-4-12 GUID shape,
// case-insensitive on input, normalized to lowercase on output. A malformed
// value (wrong shape, extra text, empty) is treated as absent — never sent to
// the server and never used to name a filter — per FR-048; the URL itself is
// left as the user typed it (not rewritten merely to clean it up).
const CANONICAL_GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function normalizeCanonicalGuid(raw: string | null): string {
  if (raw === null || !CANONICAL_GUID_RE.test(raw)) return "";
  return raw.toLowerCase();
}

// A stored price as the decimal string the price field holds, at `scale` minor
// units. Blank when the product carries no default, and blank rather than a
// guess when the scale is not known yet — a wrong scale here is a price out by
// a factor of a hundred, and an empty field simply falls back to the server's
// own default for the line.
function priceInput(defaultPriceMinorUnits: number | null, scale: number | null): string {
  if (defaultPriceMinorUnits === null || scale === null) return "";
  return (defaultPriceMinorUnits / 10 ** scale).toFixed(scale);
}

// A live line editor retains both raw inputs until an observed conflict is reloaded.
type EditorDraft = {
  orderId: string;
  itemId: string;
  quantity: number;
  price: string;
  seedQuantity: number;
  seedPrice: string;
  serverQuantity: number;
  serverPrice: number;
};

function lineDraft(order: SalesOrder, item: OrderItem): EditorDraft {
  const price = priceInput(item.unitPriceMinorUnits, order.currencyMinorUnit);
  return {
    orderId: order.id, itemId: item.id,
    quantity: item.quantity, price,
    seedQuantity: item.quantity, seedPrice: price,
    serverQuantity: item.quantity, serverPrice: item.unitPriceMinorUnits,
  };
}

function editableLine(order: SalesOrder | null, draft: EditorDraft | null): OrderItem | undefined {
  if (!order || !draft || order.status !== "Draft" || order.id !== draft.orderId) return;
  return order.items.find((item) => item.id === draft.itemId);
}

function lineChanged(item: OrderItem, draft: EditorDraft): boolean {
  return item.quantity !== draft.serverQuantity || item.unitPriceMinorUnits !== draft.serverPrice;
}

// #720 — extracted from the initial load so the post-rejection refresh applies
// the SAME filter. Two copies of this rule would drift, and the screen would
// start offering a product that Add line then 422s (codex review of #100).
function sellableProducts(products: Product[], grades: { id: string; isSaleable: boolean }[]): Product[] {
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

function lineDiscount(item: OrderItem): LineDiscount {
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
type OrderDiscount =
  | { kind: "unknown" }
  | { kind: "atList"; partial: boolean }
  | { kind: "below"; amountMinorUnits: number; percent: number | null; partial: boolean };

function orderDiscount(items: OrderItem[]): OrderDiscount {
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

// #23 + #24 (orders half): create a draft order, add/edit/remove graded lines,
// confirm (FIFO allocation), cancel drafts, browse/filter the order list.
export function SalesPage() {
  const { t } = useTranslation("sales");
  const fmt = useFormat();
  // #752 — a percent under 0.05 rounds to "0.0" and sits beside a NON-zero
  // amount, so the pair contradicts itself. Below the rendering threshold say
  // "<0.1" instead. Built from fmt.count rather than a literal so the decimal
  // separator stays the locale's — es writes 0,1.
  const discountPercent = (percent: number) =>
    percent > 0 && percent < 0.05 ? `<${fmt.count(0.1, 1)}` : fmt.count(percent, 1);
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const { farm } = useFarm();
  // Void undoes a confirmed sale — admin-only (#73); the API enforces it too.
  const { isAdmin, role } = useAuth();
  // Payments are the Sales tier (#104): Owner/Manager/Sales see and record;
  // voiding a payment stays corrective (Owner/Manager) like every other undo.
  const canSettle = isAdmin || role === "Sales";
  const { confirm, askReason, askChoice, confirmDialog } = useConfirm();
  const [customers, setCustomers] = useState<Customer[]>([]);
  // #99: lines sell PRODUCTS. Active ones feed the picker; the full list
  // (inactive included) resolves display names on existing lines.
  const [products, setProducts] = useState<Product[]>([]);
  const [allProducts, setAllProducts] = useState<Product[]>([]);
  // #445 — eggs-per-unit definitions, so the add-line form can say what the
  // quantity means BEFORE the line lands (the table row's "per tray (30 eggs)"
  // arrives too late to catch "typed 60 eggs, sold 60 trays").
  const [conversions, setConversions] = useState<EggUnitConversion[]>([]);
  // Setup reads only (customers + products). The ORDER LIST's failures live
  // in the paged-list hook and render as a banner — they must never take the
  // workspace down with them (#469).
  const [setupError, setSetupError] = useState<string | null>(null);

  // list filters (#24: status/customer/paged)
  const [statusFilter, setStatusFilter] = useState("");
  // #512 US5 (T057) — `customerId` in the URL is the SOLE source of truth for
  // the customer filter (FR-046): direct navigation, reload, edits, and
  // browser Back/Forward all flow through `searchParams` (react-router keeps
  // it live across all four). Derived fresh every render — no local copy to
  // drift from the URL. Malformed values normalize to "" (FR-048): treated as
  // absent, never sent to the server, URL left untouched.
  const [searchParams, setSearchParams] = useSearchParams();
  const customerFilter = normalizeCanonicalGuid(searchParams.get("customerId"));
  // #655 fix increment 2 — the empty-state variant and its "Clear filters"
  // reach must track every filter sent to the API (line ~289: status AND
  // customerId), not customerFilter alone, or a status-only filter that
  // matches nothing shows the truly-empty copy on a farm that has orders.
  const hasActiveFilter = Boolean(customerFilter) || Boolean(statusFilter);
  // #512 US5 (T057, FR-049) — the filter's committed identity: the row-owned
  // entity the picker's exact GET resolved for a well-formed `customerId` (or
  // a genuine user pick). A failed exact read enters the picker's own
  // `unavailable` phase — Retry re-issues ONLY the GET, Clear removes
  // `customerId` from the URL. Never a raw id, never a first-result
  // substitution, never rewritten to All.
  const [customerFilterEntity, setCustomerFilterEntity] = useState<Customer | null>(null);
  const [customerFilterSnapshot, setCustomerFilterSnapshot] = useState<PickerSnapshot<Customer>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true,
  });
  const [customerFilterPickerOpen, setCustomerFilterPickerOpen] = useState(false);
  // #512 US5 (T057, FR-050) — a URL identity change must hide the previous
  // identity's rows and the filter's own displayed name SYNCHRONOUSLY, before
  // any effect/debounce/request — not one paint later, when `usePagedList`'s
  // own reload effect would otherwise be the first thing to notice. Adjusting
  // state during render (the React-documented pattern for "reset state when a
  // prop changes") does both: `customerFilterEntity` clears so the trigger
  // never flashes the OLD name, and `customerFilterStale` gates the table
  // for exactly the render(s) between the URL changing and the new load's own
  // `reloading` taking over (cleared once that load lands, in the effect
  // below — never before, so a synchronously-hidden window isn't reshown by
  // this render alone before the replacement is ready).
  const lastCustomerFilterRef = useRef(customerFilter);
  const [customerFilterStale, setCustomerFilterStale] = useState(false);
  if (customerFilter !== lastCustomerFilterRef.current) {
    lastCustomerFilterRef.current = customerFilter;
    if (customerFilterEntity !== null) setCustomerFilterEntity(null);
    if (!customerFilterStale) setCustomerFilterStale(true);
  }

  // create-order form
  // F131: starting an order and taking a payment are discrete actions, not the
  // order builder itself — they open dialogs. Adding lines stays inline: the
  // draft panel IS the work surface.
  const [creatingOrder, setCreatingOrder] = useState(false);
  const [newOrderCustomerPickerOpen, setNewOrderCustomerPickerOpen] = useState(false);
  const [paying, setPaying] = useState(false);
  const [orderDate, setOrderDate] = useState(today);
  // #512 (T039) — the new-order customer is committed through CustomerPicker.
  // `customer` is the page-controlled committed entity; bumping
  // `customerGen` makes the engine re-sync its committed state (the explicit
  // first-customer default, a genuine pick, or a clear) so an Escape or later
  // exploration can never resurrect a stale id. `customerSnapshot.canSubmit`
  // gates BOTH the visible create control AND the create handler — a
  // disabled button alone is not the write-safety boundary. INITIAL STATE:
  // `canSubmit: false` — the dialog is inert until the engine's first
  // snapshot (or the mount-time default's controlled generation) commits an
  // entity, so a click in the brief pre-snapshot window ships nothing.
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [customerGen, setCustomerGen] = useState(0);
  const [customerSnapshot, setCustomerSnapshot] = useState<PickerSnapshot<Customer>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  // The dialog's picker is open-driven like the dialog's own focus: while the
  // dialog is up the picker is live (discovery + the requestedId seam for an
  // out-of-window external id); on close the form unmounts, so nothing
  // lingers (US3, same contract as the Expenses edit picker).

  // active draft being built
  const [active, setActiveState] = useState<SalesOrder | null>(null);
  const activeIdRef = useRef<string | null>(null);
  const activeRef = useRef<SalesOrder | null>(null);
  const [editor, setEditorState] = useState<EditorDraft | null>(null);
  const editorRef = useRef<EditorDraft | null>(null);
  // Retry identity includes the accepted edit baseline as well as the payload.
  // Keep ambiguous attempts across cancellation, but retire them when the editor
  // accepts different server values (Reload, reopening, or a clean refresh).
  const itemUpdateAttempts = useRef(new Map<string, {
    quantity: number; unitPriceMinorUnits: number; key: string;
    serverQuantity: number; serverPrice: number;
  }>());
  // Async publications read the latest typing/cancellation, not their starting render.
  const setEditor = useCallback((draft: EditorDraft | null) => {
    if (draft) {
      const scope = `update-item:${draft.itemId}`;
      const attempt = itemUpdateAttempts.current.get(scope);
      if (attempt && (attempt.serverQuantity !== draft.serverQuantity || attempt.serverPrice !== draft.serverPrice)) {
        itemUpdateAttempts.current.delete(scope);
      }
    }
    editorRef.current = draft;
    setEditorState(draft);
  }, []);
  const setActive = useCallback((order: SalesOrder | null) => {
    const draft = editorRef.current;
    const item = editableLine(order, draft);
    if (!order || !draft || !item) {
      setEditor(null);
    } else if (draft.quantity === draft.seedQuantity && draft.price === draft.seedPrice) {
      setEditor(lineDraft(order, item));
    }
    activeRef.current = order;
    activeIdRef.current = order?.id ?? null;
    setActiveState(order);
  }, [setEditor]);
  const [productId, setProductId] = useState("");
  const [unit, setUnit] = useState("Egg");
  const [qty, setQty] = useState(30);
  // NumberField owns its own input, so labels point at it by id (F134 idiom).
  const fieldId = useId();
  const addQtyId = `${fieldId}-qty`;
  const editQtyId = `${fieldId}-edit-qty`;
  const [price, setPrice] = useState("");

  // ONE scale for every price this screen reads or writes (#123). The two
  // sources are the same currency by construction — an order is created in the
  // farm's currency, and since #159 a priced product locks the farm to it — so
  // this is one number reached two ways, not a choice between two answers.
  //
  // It used to be a choice: the prefill divided by the PRODUCT's minor unit
  // while the submit multiplied by the ORDER's. Latent while they cannot
  // differ, and an accepted prefill 100x out on the day they can — arriving as
  // an EXPLICIT price, which is exactly the case the server's
  // ProductPriceCurrencyMismatch guard does not fire on.
  const farmScale = farm?.currencyMinorUnit ?? null;
  const priceScale = active?.currencyMinorUnit ?? farmScale;

  const editingLine = editableLine(active, editor);
  const editConflict = !!editor && !!editingLine && lineChanged(editingLine, editor);
  const reloadEditor = () => {
    const order = activeRef.current;
    const item = editableLine(order, editorRef.current);
    if (order && item) setEditor(lineDraft(order, item));
  };

  // Other idempotency keys are bound to (action, target) and rotated ONLY after the whole
  // action (write + refresh) succeeds: a retry after any failure — including a
  // lost response or a failed follow-up read — replays the same key, so the
  // server dedupes instead of duplicating the write.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const [message, setMessage] = useState<string | null>(null);
  // #703 — the flight guard (#236), the per-place message slots (#479) and the
  // dialog-session generation (#477 part 2) are composed by one shared hook.
  // Sales learned every rule of this the hard way over five review rounds
  // (#474 → #477 → #479 → #625 → #702); the rules and the incidents that
  // earned them live with the hook. Sales-specific is only WHICH scopes own a
  // dialog, and that the page-level success message clears on each attempt.
  const { busy, isPending, errors, run, openDialog, dismissDialog, startLoad } = useDialogAction(
    ["create-order", "record-payment", "order-panel"],
    { onAttempt: () => setMessage(null) },
  );
  // Pulled out for the payments effect's dependency list: it is stable, and
  // naming it is what lets that effect declare its real dependencies
  // (`dismissDialog` is stable by the hook's own construction).
  const { setPage: setPageError } = errors;

  // Payments (#89, admin-only money data) — settlement state of the open
  // confirmed order.
  const [payments, setPayments] = useState<OrderPayments | null>(null);
  const [payDate, setPayDate] = useState(today);
  const [payAmount, setPayAmount] = useState("");
  // Named once: the initial value and the new-session reset below must not
  // drift apart, which is exactly what two "Cash" literals would allow.
  const DEFAULT_PAY_METHOD = "Cash";
  const [payMethod, setPayMethod] = useState(DEFAULT_PAY_METHOD);
  const [payRef, setPayRef] = useState("");
  const [payNote, setPayNote] = useState("");

  // #512 US4 (T048/T052) — an order's own name, independent of the picker's
  // capped customer list: the row-owned `customerName` the endpoint's scoped
  // bulk read already resolved, or the translated unavailable label. Never
  // the catalog `customers` list and never an id fragment
  // (contracts/http-api.md: "Required names are never replaced with
  // identifier fragments").
  const rowCustomerName = (o: { customerName?: string | null }) =>
    o.customerName ?? t("rowCustomerUnavailable");
  const productName = (id: string) => allProducts.find((p) => p.id === id)?.name ?? id.slice(0, 8);

  // #445 — eggs per PACKED selling unit. null when no active definition
  // exists — the UI then shows nothing extra rather than a wrong number, and
  // the server's own check decides at add time. Callers exclude the per-egg
  // unit by IDENTITY, never by factor value: only "Individual" is pinned to 1
  // server-side, so a packed unit deliberately defined as 1 egg/unit is a
  // real (nonstandard) configuration that must stay visible at entry time
  // (codex review of #445) — which also means the "Egg"→"Individual" lookup
  // the server does is not needed here, since "Egg" never annotates.
  const eggsPerUnit = (sellingUnit: string): number | null => {
    const c = conversions.find((x) => x.unitCode === sellingUnit && x.active);
    return c?.eggsPerUnit ?? null;
  };
  // Lowercased to match the row display's `perUnit` convention ("per tray").
  // The membership guard is what lets the typed i18n key accept the template:
  // `unit${SellingUnit}` is a closed union of real catalog keys, while an
  // unrecognized unit string (a future enum value this build predates) falls
  // back to its raw name rather than a missing-key render.
  const unitWord = (sellingUnit: string) =>
    (isSellingUnit(sellingUnit) ? t(`unit${sellingUnit}`) : sellingUnit).toLowerCase();

  // #469 — this list had no request sequencing, and its failure was fatal:
  // any rejection (including one from a request the user had already moved
  // past) set a `loadError` that NOTHING ever cleared, replacing the whole
  // workspace — an order being edited included — for the rest of the session.
  // The hook raises an error only from the current request and clears it on
  // the next successful load; the screen now renders it as a banner beside
  // the work rather than instead of it.
  const orders = usePagedList({
    fetchPage: useCallback(
      (offset: number, limit: number) => listOrders({
        status: statusFilter || undefined,
        customerId: customerFilter || undefined,
        limit,
        offset,
      }),
      [statusFilter, customerFilter],
    ),
    pageSize: PAGE,
    errorText: () => i18n.t("sales:loadOrdersFailed"),
  });

  // #512 US5 (T057, FR-050) — the synchronous hide above lasts until the
  // filter's OWN replacement load actually lands (`reloading` returns to
  // false), not just until the next render — a load can still be in flight
  // when this effect first sees the stale flag.
  useEffect(() => {
    if (customerFilterStale && !orders.reloading) setCustomerFilterStale(false);
  }, [customerFilterStale, orders.reloading]);

  useEffect(() => {
    // includeInactive: existing order lines may reference deactivated
    // products, and their names must still resolve. The add-item picker
    // filters back down to sellable: active products whose mapped grade is
    // still active + saleable — same rule the server enforces, so the picker
    // never offers something Add line would 422 (codex review of #100).
    Promise.all([
      listCustomers(),
      listProducts({ includeInactive: true }),
      listEggGrades(),
    ])
      .then(([c, p, g]) => {
        setCustomers(c);
        setAllProducts(p);
        const sellable = sellableProducts(p, g);
        setProducts(sellable);
        if (c.length > 0) {
          // #512 (T039) — the explicit first-customer default (FR-037): the
          // exact first customer from the page's own read, committed through
          // a controlled generation the moment the setup read settles. The
          // engine admits an entity it already knows as-is (no spurious
          // exact GET), and the create handler ships `customer.id` — never a
          // raw id the picker has not committed.
          setCustomer(c[0]);
          setCustomerGen((g) => g + 1);
        }
        if (sellable.length > 0) {
          const first = sellable[0];
          setProductId(first.id);
          setUnit(first.defaultUnit);
          // Prefill the price from the FIRST product too — the hard-coded
          // starter value used to shadow the product default until the user
          // changed the selection (codex review of #100). At the farm's scale:
          // there is no order yet, and the order this will be typed into will
          // carry the farm's currency.
          setPrice(priceInput(first.defaultPriceMinorUnits, farmScale));
        }
      })
      .catch(() => setSetupError(i18n.t("sales:loadSalesDataFailed")));
    // Separate from the Promise.all above ON PURPOSE: the conversions only
    // feed supplementary display (the live "= N eggs" hint and the option
    // annotations), so a failed read degrades to the pre-#445 labels instead
    // of blocking the whole screen. A genuinely missing conversion still 422s
    // server-side at add time (SalesOrder.NoUnitConversion).
    listEggUnitConversions().then(setConversions).catch(() => {});
  }, []);


  const activeId = active?.id ?? null;
  const activeStatus = active?.status ?? null;
  useEffect(() => {
    // Cleared FIRST, unconditionally: while the new order's payments load (or
    // if the load fails), stale rows from the previous order must never stay
    // actionable — their Void buttons would target the wrong order's money
    // (codex review of #90).
    setPayments(null);
    // …and so does the payment form, for the same reason one layer up: it
    // belongs to the order that was open, not to the screen. Left open,
    // `paying` would reopen it on the NEXT order unasked, showing that order's
    // money under the previous order's failure — its key says "record-payment",
    // not which order (codex review of #481).
    //
    // The slot is emptied here, not merely closed over. Under #478 the trigger
    // cleared the entry on the way back in, so a clear here killed no mutant;
    // #479 moved that clear onto the dismissal, and THIS path is not one — it
    // is the screen closing the form out from under the user. Without the line
    // below, a 422 about the previous order's money survives the switch and is
    // waiting inside the form when they open it on this order's.
    //
    // `dismissDialog`, not `clearDialog`: a payment write can still be out
    // when this runs, so the slot is emptied, that attempt muted, AND its
    // session ended, together (#703 round 1 — every edge does both). Clearing
    // alone would let the rejection settle into the slot afterwards, to be
    // found by whoever opens a payment form next; muting without ending the
    // session would let the write's success act on a form opened later.
    //
    // An earlier version used `clearDialog` and argued the mute was
    // unreachable, because the row's open button is `disabled={busy}`. That
    // enumeration of what can change these deps was wrong twice over — the
    // panel's own Close is ungated (and #480 already established the backdrop
    // stops a mouse, not a screen reader's virtual cursor), and `canSettle`
    // flips with no button at all when a transparent 401 refresh re-derives the
    // role mid-write. Two misses of one shape means the method is wrong, so
    // this stops reasoning about reachability: `abandon` is correct whether or
    // not a write is out, and the next `beginAttempt` un-mutes so a later form
    // can still fail normally.
    //
    // Stated honestly, because a mutation says so: swapping this back to
    // `clearDialog` breaks NO test, and no test can be written that it would
    // break — every route back into a payment form re-runs this effect, which
    // clears the slot on the way in regardless. The choice buys the removal of
    // an argument that has been wrong twice, not an observable fix. Deleting
    // the line altogether IS caught, by the reopen test below.
    setPaying(false);
    dismissDialog("record-payment");
    if (activeId === null || activeStatus !== "Confirmed" || !canSettle) return;
    let cancelled = false;
    listOrderPayments(activeId)
      .then((p) => { if (!cancelled) setPayments(p); })
      .catch(() => {
        if (!cancelled) setPageError(i18n.t("sales:loadPaymentsFailed"));
      });
    return () => { cancelled = true; };
    // `setPageError` is destructured above and `dismissDialog` is stable in
    // the hook; both are listed here rather than depending on `errors` — that
    // object is rebuilt every render, so naming it would re-run this on every
    // render and re-fetch the payments. There is no eslint in this package to
    // have caught either.
  }, [activeId, activeStatus, canSettle, dismissDialog, setPageError]);

  // Exact decimal parsing in the ORDER's denomination (no float multiply —
  // #88 review); excess decimals are rejected, not silently rounded.
  const toMinor = (display: string, minor: number) => {
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
  };

  // `run` (from useDialogAction) also wraps two READS — "open:<id>" and "more"
  // — which keep the flight guard but deliberately get no BusyButton treatment
  // (#236 is writes). Pending scopes are independent of the idempotency key
  // scopes built inside each action (nothing couples the two).
  //
  // Closing a dialog is the screen's state; the two bookkeeping calls that end
  // its session and mute its attempt are the hook's (`dismissDialog`). Cancel
  // stays live during `busy`, and Escape and the backdrop dismiss too (#474).
  const closeNewOrder = () => {
    setNewOrderCustomerPickerOpen(false);
    setCreatingOrder(false);
    dismissDialog("create-order");
  };
  const closePayment = () => {
    setPaying(false);
    dismissDialog("record-payment");
  };

  const closeOrderPanel = () => {
    dismissDialog("order-panel");
    setActive(null);
  };

  const onCreateOrder = () => run("create-order", async (current) => {
    // #512 (T039) — the handler's own guard: canSubmit is the write-safety
    // boundary (a disabled button alone is not). An exploring/uninitialized
    // or unavailable picker must not ship a stale committed id.
    if (!customer || !customerSnapshot.canSubmit) return;
    // runWrite claims the list ticket before the POST, so a filter change
    // made while it is in flight keeps the view (#469).
    await orders.runWrite(async () => {
      const created = await createOrder({ customerId: customer.id, orderDate }, keyFor("create-order"));
      // The key rotates the moment the WRITE lands — the same rule the payment
      // path states (#90). Releasing it after the follow-up read instead left a
      // spent key stranded whenever that read failed, and the next order then
      // replayed this one, so the customer the user actually chose never got
      // an order (codex review of this branch).
      clearKey("create-order");
      // Superseded: the order exists and the list write stands, but the panel
      // belongs to whatever session is on screen now (#477).
      if (!current()) return;
      const loaded = await getOrder(created.id);
      // Checked AGAIN, after the second await. The first version of this fix
      // checked once and then wrote the result of a further round trip, which
      // is the same hijack one hop later: the POST lands while the user is
      // still here, the GET is issued, and only THEN do they cancel and reopen.
      // A gate before an await says nothing about the state after it.
      if (!current()) return;
      setActive(loaded);
    });
    if (!current()) return;
    setNewOrderCustomerPickerOpen(false);
    setCreatingOrder(false); // only on success — a throw keeps the dialog up
  });

  const onAddItem = () => run("add-item", async () => {
    if (!active) return;
    const id = active.id;
    // #398 — sales quantities are whole selling units; reject a fractional
    // value BEFORE sending rather than letting the server's JSON binding
    // fail with an internal parameter-binding message. NumberField's typed
    // input isn't step-constrained (no wrapping <form> — see the comment
    // below), so `qty` can legitimately hold e.g. 2.5 here.
    if (!Number.isInteger(qty)) throw new Error(i18n.t("sales:quantityMustBeWholeNumber"));
    // Empty price → omit it: the server falls back to the product's default.
    let minorUnits: number | undefined;
    if (price.trim() !== "") {
      minorUnits = parseMoneyToMinorUnits(price, active.currencyMinorUnit);
      if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error(i18n.t("sales:invalidUnitPrice"));
    }
    const scope = `add-item:${id}`;
    // #445 — bind the previewed factor to the write: if an admin redefined
    // the unit after this page read its conversions, the server refuses
    // (SalesOrder.UnitDefinitionChanged) instead of recording a QuantityBase
    // different from the "= N eggs" the seller saw. undefined when nothing
    // was previewed (per-egg unit, or no/failed conversions read).
    const previewed = unit === "Egg" ? null : eggsPerUnit(unit);
    try {
      await addOrderItem(id,
        {
          productId, quantity: qty, unit, unitPriceMinorUnits: minorUnits,
          expectedEggsPerUnit: previewed ?? undefined,
          ...(() => {
            // #720 — presence matters: "the seller saw no list price" is an
            // expectation, and it is not the same as having no opinion.
            const shown = products.find((p) => p.id === productId);
            if (!shown) return {};
            return shown.defaultPriceMinorUnits === null
              ? { expectedListPriceIsUnset: true }
              : { expectedListUnitPriceMinorUnits: shown.defaultPriceMinorUnits };
          })(),
        },
        keyFor(scope));
    } catch (err) {
      // Any server rejection may mean the conversions moved under us (the
      // UnitDefinitionChanged case) — refresh them so the preview and the
      // next attempt use the current factors instead of looping on stale
      // ones. Fire-and-forget: the thrown error still surfaces normally.
      // #720 — products too, not just conversions: a ListPriceChanged refusal
      // means the catalogue moved, and without this the retry loops on the
      // same stale price forever.
      if (err instanceof ApiError) {
        listEggUnitConversions().then(setConversions).catch(() => {});
        Promise.all([listProducts({ includeInactive: true }), listEggGrades()])
          .then(([p, g]) => { setAllProducts(p); setProducts(sellableProducts(p, g)); })
          .catch(() => {});
      }
      throw err;
    }
    const refreshed = await getOrder(id);
    if (activeIdRef.current === id) setActive(refreshed);
    clearKey(scope);
  });

  const onUpdateItem = (itemId: string) => run(`update-item:${itemId}`, async () => {
    const order = activeRef.current;
    const draft = editorRef.current;
    const item = editableLine(order, draft);
    if (!order || !draft || !item || draft.itemId !== itemId || lineChanged(item, draft)) return;
    const id = order.id;
    // #398 — same whole-number guard as the add-line control, above.
    if (!Number.isInteger(draft.quantity)) throw new Error(i18n.t("sales:quantityMustBeWholeNumber"));
    const minorUnits = parseMoneyToMinorUnits(draft.price, order.currencyMinorUnit);
    if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error(i18n.t("sales:invalidUnitPrice"));
    const scope = `update-item:${itemId}`;
    const previous = itemUpdateAttempts.current.get(scope);
    const attempt = previous?.quantity === draft.quantity && previous.unitPriceMinorUnits === minorUnits
      ? previous
      : {
        quantity: draft.quantity, unitPriceMinorUnits: minorUnits, key: newId(),
        serverQuantity: draft.serverQuantity, serverPrice: draft.serverPrice,
      };
    itemUpdateAttempts.current.set(scope, attempt);
    await updateOrderItem(id, itemId,
      { quantity: draft.quantity, unitPriceMinorUnits: minorUnits }, attempt.key);
    if (editorRef.current === draft) setEditor(null);
    const refreshed = await getOrder(id);
    if (activeIdRef.current === id) setActive(refreshed);
    itemUpdateAttempts.current.delete(scope);
  });

  const onRemoveItem = (itemId: string) => run(`remove-item:${itemId}`, async () => {
    if (!active) return;
    const id = active.id;
    const scope = `remove-item:${itemId}`;
    await removeOrderItem(id, itemId, keyFor(scope));
    const refreshed = await getOrder(id);
    if (activeIdRef.current === id) setActive(refreshed);
    clearKey(scope);
  });

  // One-way actions (#59). Confirm BEFORE run() so buttons don't flash
  // disabled while the user decides.
  const onConfirm = async () => {
    if (!active) return;
    // #721 — a below-list order must carry a reason, and the server refuses one
    // on an order that has nothing below list, so the same predicate decides
    // both which dialog opens and whether a body is sent. lineDiscount is that
    // predicate already; a second one here could drift from it.
    const belowList = active.items.some((item) => lineDiscount(item).kind === "below");
    let body: { discountReasonCode: string; discountReasonNote?: string } | undefined;
    if (belowList) {
      const picked = await askChoice({
        title: i18n.t("sales:discountReasonTitle"),
        body: i18n.t("sales:discountReasonBody"),
        confirmLabel: i18n.t("sales:confirmOrderConfirmLabel"),
        choiceLabel: i18n.t("sales:discountReasonLabel"),
        choices: DISCOUNT_REASON_VALUES.map((value) => ({
          value,
          label: discountReasonLabel(value),
        })),
        noteRequiredFor: ["Other"],
        noteLabel: i18n.t("sales:discountReasonNoteLabel"),
        noteRequiredMessage: i18n.t("sales:discountReasonNoteRequired"),
        choiceRequiredMessage: i18n.t("sales:discountReasonRequired"),
      });
      if (picked === null) return;
      body = { discountReasonCode: picked.value };
      if (picked.note !== null) body.discountReasonNote = picked.note;
    } else {
      const ok = await confirm({
        title: i18n.t("sales:confirmOrderTitle"),
        body: i18n.t("sales:confirmOrderBody"),
        confirmLabel: i18n.t("sales:confirmOrderConfirmLabel"),
      });
      if (!ok) return;
    }
    const id = active.id;
    void run(`confirm:${id}`, async () => {
      const scope = `confirm:${id}`;
      await orders.runWrite(async () => {
        await confirmOrder(id, body, keyFor(scope));
        const refreshed = await getOrder(id);
        if (activeIdRef.current === id) {
          setActive(refreshed);
          setMessage(i18n.t("sales:orderConfirmed", { ref: refreshed.referenceNumber }));
        }
      });
      clearKey(scope);
    });
  };

  const onCancel = async () => {
    // Cancel is a status change: the order keeps its lines but becomes
    // read-only and can't be confirmed.
    const ok = await confirm({
      title: i18n.t("sales:cancelDraftTitle"),
      body: i18n.t("sales:cancelDraftBody"),
      confirmLabel: i18n.t("sales:cancelDraft"),
      destructive: true,
    });
    if (!ok || !active) return;
    const id = active.id;
    void run(`cancel:${id}`, async () => {
      const scope = `cancel:${id}`;
      await orders.runWrite(async () => {
        await cancelOrder(id, keyFor(scope));
        if (activeIdRef.current === id) {
          setActive(null);
          setMessage(i18n.t("sales:draftOrderCancelled"));
        }
      });
      clearKey(scope);
    });
  };

  // Undo of a mistaken confirm (#60). Reason prompt doubles as the confirm
  // dialog, hoisted above run() like the other one-way actions; cancelling the
  // prompt aborts the void.
  const refreshPayments = async (orderId: string) => {
    const refreshed = await listOrderPayments(orderId);
    if (activeIdRef.current === orderId) setPayments(refreshed);
  };

  const onRecordPayment = () => void run("record-payment", async (current) => {
    if (!active || !payments) return;
    const minorUnits = toMinor(payAmount, payments.currencyMinorUnit);
    const scope = `pay:${active.id}`;
    await recordPayment(active.id, {
      paymentDate: payDate,
      amountMinorUnits: minorUnits,
      method: payMethod,
      referenceNumber: payRef.trim() || null,
      note: payNote.trim() || null,
    }, keyFor(scope));
    // The key rotates the moment the WRITE lands — if it survived until the
    // refresh below succeeded, a failed refresh would make the NEXT payment
    // reuse it and silently replay this 201 instead of recording new money
    // (codex review of #90). The form reset before the refresh (#88 review)
    // covers the duplicate-resubmit direction.
    clearKey(scope);
    // The resets and the refresh keep the order #88 put them in. A SUPERSEDED
    // attempt skips the resets, because the fields now belong to a session the
    // user is typing into; opening the dialog clears them instead, so the spent
    // values cannot be resubmitted under a fresh key (codex review).
    if (current()) {
      setPayAmount("");
      setPayRef("");
      setPayNote("");
    }
    await refreshPayments(active.id);
    // NOT gated, deliberately, and this is where #477's own wording is wrong:
    // it calls the message "stray". The money was recorded. Withholding the
    // confirmation because the user closed the dialog leaves them believing it
    // did not happen, and the likely next act is paying twice. The message is
    // page-owned and renders outside the dialog, so it has somewhere honest to
    // land whether or not that session still exists (codex review).
    setMessage(i18n.t("sales:paymentRecorded"));
    if (!current()) return;
    setPaying(false); // only on success — a throw keeps the dialog up
  });

  const onVoidPayment = async (paymentId: string, version: number) => {
    const reason = await askReason({
      title: i18n.t("sales:voidPaymentTitle"),
      body: i18n.t("sales:voidPaymentBody"),
      confirmLabel: i18n.t("sales:voidPaymentConfirmLabel"),
      destructive: true,
    });
    if (reason === null) return;
    void run(`void-payment:${paymentId}`, async () => {
      if (!active) return;
      const id = active.id;
      const scope = `void-payment:${paymentId}`;
      try {
        await voidPayment(paymentId, { version, reason }, keyFor(scope));
        clearKey(scope);
      } catch (err) {
        // Version-guarded: any SERVER response settles the attempt (the base
        // version prevents double-apply); only transport failures keep the key.
        if (err instanceof ApiError) clearKey(scope);
        throw err;
      }
      await refreshPayments(id);
      setMessage(i18n.t("sales:paymentVoided"));
    });
  };

  const onVoid = async () => {
    const reason = await askReason({
      title: i18n.t("sales:voidOrderTitle"),
      body: i18n.t("sales:voidOrderBody"),
      confirmLabel: i18n.t("sales:voidOrderConfirmLabel"),
      destructive: true,
    });
    if (reason === null || !active) return;
    const id = active.id;
    void run(`void:${id}`, async () => {
      const scope = `void:${id}`;
      await orders.runWrite(async () => {
        await voidOrder(id, reason, keyFor(scope));
        const refreshed = await getOrder(id);
        if (activeIdRef.current === id) {
          setActive(refreshed);
          setMessage(i18n.t("sales:orderVoided", { ref: refreshed.referenceNumber }));
        }
      });
      clearKey(scope);
    });
  };

  // Always fetch fresh on open — the list row may be stale relative to
  // mutations made through the panel since the list was loaded.
  const onOpen = (id: string) => run(`open:${id}`, async () => {
    const current = startLoad("order-panel");
    const loaded = await getOrder(id);
    if (current()) setActive(loaded);
  });

  // A list failure no longer replaces the workspace: it renders as a banner
  // beside it (below), so a transient blip cannot discard an order the user
  // is part-way through editing (#469). Only the setup reads — customers and
  // products, without which no form on this screen can function — still gate
  // the page.
  if (setupError) return <section><h2>{t("title")}</h2><p className="error">{setupError}</p></section>;
  if (orders.rows === null) return <section><h2>{t("title")}</h2><p className="muted">{t("loading")}</p></section>;

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {/* #655 — also withheld exactly when the truly-empty state below is
            offering this same action (never for the filtered-empty branch,
            which offers "Clear filters" instead — no duplicate there). */}
        {customers.length > 0 && !(orders.rows.length === 0 && !hasActiveFilter) && (
          // Re-seeded on open, not only at mount: a tab left open across
          // farm-midnight would otherwise offer yesterday as the order date
          // while the picker's own ceiling had already moved on (codex review
          // of #123).
          <button type="button" onClick={() => {
            // Nothing to clear on the way in: #479 moved that onto the
            // dismissal, so the slot is already empty before a reopen.
            setOrderDate(today);
            setNewOrderCustomerPickerOpen(true);
            openDialog("create-order"); // a new session — see #477
            setCreatingOrder(true);
          }}>
            <Plus size={16} aria-hidden /> {t("newOrder")}
          </button>
        )}
      </div>

      {customers.length === 0 && (
        <p className="muted">{t("addCustomerFirst")}</p>
      )}

      {/* #612 — persistent and generic on purpose: the farm has opted into
          all-farm-flocks allocation, but THIS caller is still a restricted
          Worker, so confirming may still refuse for a shortfall in flocks
          they cannot see. Never names a flock, grade, or quantity. */}
      {farm?.showFarmWideSaleAllocationNotice && (
        <p className="hint" role="status">{t("farmWideAllocationNotice")}</p>
      )}

      {/* Deliberately NOT a <form>: these controls were button-driven, so
          wrapping them in one would newly enforce min/step and swallow the
          screen's own money messages (codex review of #132). */}
      <Dialog open={creatingOrder} title={t("newOrder")} onClose={closeNewOrder}>
        <div className="form-grid">
          <CustomerPicker
            label={t("customer")}
            required
            // Start open for immediate discovery, then close after commit so
            // the absolutely-positioned listbox cannot cover the remaining
            // dialog controls. The committed trigger can reopen it to change
            // customer without discarding the draft fields.
            open={newOrderCustomerPickerOpen}
            controlledCommitted={customer}
            controlledGeneration={customerGen}
            onSnapshot={setCustomerSnapshot}
            onCommit={(c) => {
              setCustomer(c);
              setCustomerGen((g) => g + 1);
              setNewOrderCustomerPickerOpen(false);
            }}
            onEscape={() => setNewOrderCustomerPickerOpen(false)}
            onOutsideClick={() => setNewOrderCustomerPickerOpen(false)}
            trigger={
              <button type="button" className="named-picker-trigger"
                onClick={() => setNewOrderCustomerPickerOpen(true)}>
                {customer ? customer.name : t("pickCustomerOption")}
              </button>
            }
          />
          <label>{t("date")}
            <input type="date" value={orderDate} max={today}
              onChange={(e) => setOrderDate(e.target.value)} />
          </label>
          {/* #474 — this copy lives INSIDE the dialog, which renders nothing
              while closed, so the page's `!creatingOrder` condition hid the
              message exactly when the dialog it belongs to was up. The scope
              test replaces it: the dialog reports its OWN write, never
              whatever else happened to fail underneath it. role="alert"
              because focus is trapped in the panel and nothing else announces
              the failure. */}
          <DialogError errors={errors} scope="create-order" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeNewOrder}>{tc("cancel")}</button>
            <BusyButton disabled={busy || !customer || !customerSnapshot.canSubmit}
              busy={isPending("create-order")}
              onClick={onCreateOrder}>{t("newDraftOrder")}</BusyButton>
          </div>
        </div>
      </Dialog>

      {active && (
        <div className="order-panel">
          <h3>
            {active.referenceNumber} — {rowCustomerName(active)}{" "}
            <span className={active.status === "Draft" ? "muted" : "warn"}>
              [{statusLabel(active.status)}]
            </span>
          </h3>

          {active.items.length > 0 && (
            <table className="data">
              <thead><tr><th>{t("product")}</th><th className="num">{t("qty")}</th><th className="num">{t("eggs")}</th><th className="num">{t("listPrice")}</th><th className="num">{t("unitPrice")}</th><th className="num">{t("discount")}</th><th className="num">{t("lineTotal")}</th><th></th></tr></thead>
              <tbody>
                {active.items.map((i) => {
                  // #752 — ONE discount per row. The tint, the chip and the
                  // Discount cell all read this, so they cannot disagree.
                  // While this row is being edited it describes the TYPED
                  // price, the same way #445 made the eggs column live: a row
                  // that still claims its saved discount while you retype the
                  // price is telling you about a line that no longer exists.
                  // An unparseable box (mid-keystroke, empty) falls back to
                  // the saved line rather than flickering to "no list price".
                  const editingThis = !!editor && editingLine?.id === i.id;
                  const typed = editingThis
                    ? parseMoneyToMinorUnits(editor.price, active.currencyMinorUnit)
                    : Number.NaN;
                  const discount = editingThis && Number.isFinite(typed)
                    ? lineDiscount({ ...i, unitPriceMinorUnits: typed, quantity: editor.quantity })
                    : lineDiscount(i);
                  // Rendered identically in BOTH branches below. Two copies
                  // of this that drifted apart is what #752 was.
                  const discountCell = discount.kind === "below"
                    ? <span className="discount">
                        {`${fmt.money(discount.amountMinorUnits, i.currencyCode, i.currencyMinorUnit)} · ${discountPercent(discount.percent)}%`}
                      </span>
                    : discount.kind === "above" ? t("aboveList")
                      : discount.kind === "none" ? t("noListPrice")
                        : "—";
                  return (
                  <tr key={i.id} className={discount.kind === "below" ? "discounted" : undefined}>
                    <td>{productName(i.productId)}{" "}
                      <span className="muted">{t("perUnit", { unit: i.unit.toLowerCase() })}
                        {i.baseUnitFactor > 1 ? ` ${t("eggsCount", { count: i.baseUnitFactor })}` : ""}</span>
                      {/* #723 — the text marker, beside the product rather than
                          in the Discount cell, so it is legible on a row whose
                          numeric cells are being scanned as a column. */}
                      {discount.kind === "below" && (
                        <> <span className="badge badge-warn">{t("belowListBadge")}</span></>
                      )}
                      {discount.kind === "none" && (
                        <> <span className="badge">{t("noListPrice")}</span></>
                      )}</td>
                    {editor && editingLine?.id === i.id ? (
                      <>
                        <td className="num">
                          {/* No visible label in the cell — the sr-only one names
                              the input; the buttons carry their own names. */}
                          <label className="sr-only" htmlFor={editQtyId}>{t("editQuantityAriaLabel")}</label>
                          <NumberField id={editQtyId} label={t("editQuantityAriaLabel").toLowerCase()}
                            value={editor.quantity} onChange={(quantity) => {
                              const draft = editorRef.current;
                              if (draft) setEditor({ ...draft, quantity: typeof quantity === "function" ? quantity(draft.quantity) : quantity });
                            }} min={1} />
                        </td>
                        {/* #445 — live: the eggs column tracks the edited
                            quantity instead of going blank, so a unit/count
                            mix-up is visible mid-edit too. */}
                        <td className="num muted">{fmt.count(i.baseUnitFactor * editor.quantity)}</td>
                        <td className="num muted">
                          {i.listUnitPriceMinorUnits === null
                            ? "—"
                            : fmt.money(i.listUnitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}
                        </td>
                        <td className="num"><input className="cell" type="number" min={0}
                          aria-label={t("editUnitPriceAriaLabel")}
                          step={10 ** -active.currencyMinorUnit} value={editor.price}
                          onChange={(e) => {
                            const draft = editorRef.current;
                            if (draft) setEditor({ ...draft, price: e.target.value });
                          }} /></td>
                        {/* #752 — was a flat "—", which contradicted the tint
                            and chip this same row still carried, and erased an
                            above-list line's ONLY marker for the whole edit. */}
                        <td className="num">{discountCell}</td>
                        <td className="num">—</td>
                        <td>
                          <BusyButton className="link" disabled={busy || editConflict} busy={isPending(`update-item:${i.id}`)}
                            onClick={() => onUpdateItem(i.id)}>{t("save")}</BusyButton>
                          <button className="link" onClick={() => setEditor(null)}>{t("cancelEdit")}</button>
                          {editConflict && (
                            <div role="status">
                              {t("editConflict")} {" "}
                              <button className="link" onClick={reloadEditor}>{t("reloadLine")}</button>
                            </div>
                          )}
                        </td>
                      </>
                    ) : (
                      <>
                        <td className="num">{fmt.count(i.quantity)}</td>
                        <td className="num">{fmt.count(i.quantityBase)}</td>
                        <td className="num">
                          {i.listUnitPriceMinorUnits === null
                            ? "—"
                            : discount.kind === "below"
                              ? <s>{fmt.money(i.listUnitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}</s>
                              : fmt.money(i.listUnitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}
                        </td>
                        <td className="num">{fmt.money(i.unitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td className="num">{discountCell}</td>
                        <td className="num">{fmt.money(i.unitPriceMinorUnits * i.quantity, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td>
                          {active.status === "Draft" && (
                            <>
                              <button className="link" disabled={busy} onClick={() => {
                                setEditor(lineDraft(active, i));
                              }}>{t("edit")}</button>
                              <BusyButton className="link" disabled={busy} busy={isPending(`remove-item:${i.id}`)}
                                onClick={() => onRemoveItem(i.id)}>{t("remove")}</BusyButton>
                            </>
                          )}
                        </td>
                      </>
                    )}
                  </tr>
                  );
                })}
              </tbody>
            </table>
          )}
          {/* #723 — the order's give-away, directly above the total it was
              taken from. A sibling <p>, not a <tfoot>: the order total has
              never been a table footer and the only <tfoot> in the app is
              ReportsPage's. Rendered ONLY for a below-list order — #723's
              acceptance is that an at-list order carries no treatment at all. */}
          {(() => {
            const orderLevel = orderDiscount(active.items);
            // Round 1 — read `partial` BEFORE bailing on kind. An order with
            // nothing discounted but a line we cannot measure is not an at-list
            // order, and rendering nothing here let it read as one.
            if (orderLevel.kind === "atList" && orderLevel.partial) {
              return (
                <p className="discount-note" data-testid="order-discount-partial">
                  {t("discountPartialOnly")}
                </p>
              );
            }
            // Round 2 — `unknown` reached this bail and rendered nothing, while
            // the Orders list said "Unknown" for the same order. Two screens
            // disagreeing about whether an order is measurable is worse than
            // either answer alone.
            if (orderLevel.kind === "unknown") {
              return (
                <p className="discount-note" data-testid="order-discount-unknown">
                  {t("discountUnknownOrder")}
                </p>
              );
            }
            if (orderLevel.kind !== "below") return null;
            const amount = fmt.money(orderLevel.amountMinorUnits, active.currencyCode, active.currencyMinorUnit);
            return (
              <p className="discount" data-testid="order-discount">
                {orderLevel.percent === null
                  ? t("discountTotalNoPct", { amount })
                  : t("discountTotal", { amount, percent: discountPercent(orderLevel.percent) })}
                {orderLevel.partial ? ` (${t("discountPartialNote")})` : ""}
              </p>
            );
          })()}
          {/* #721 — the reason the order was allowed below list, beside the
              give-away it explains. Absent on an order confirmed before that
              shipped: no backfill, so nothing here means "not recorded". */}
          {active.discountReasonCode && (
            <p className="discount-note" data-testid="order-discount-reason">
              {active.discountReasonNote
                ? t("discountReasonSummaryWithNote", {
                    reason: discountReasonLabel(active.discountReasonCode),
                    note: active.discountReasonNote,
                  })
                : t("discountReasonSummary", {
                    reason: discountReasonLabel(active.discountReasonCode),
                  })}
            </p>
          )}
          <p><strong>{t("orderTotal", { amount: fmt.money(active.totalMinorUnits, active.currencyCode, active.currencyMinorUnit) })}</strong></p>

          {active.status === "Draft" && (
            <>
              <div className="form-grid">
                <label>{t("product")}
                  <select value={productId} onChange={(e) => {
                    setProductId(e.target.value);
                    const p = products.find((x) => x.id === e.target.value);
                    if (p) {
                      setUnit(p.defaultUnit);
                      setPrice(priceInput(p.defaultPriceMinorUnits, priceScale));
                    }
                  }}>
                    {products.map((p) => {
                      // Unit size visible BEFORE quantity entry starts (#445):
                      // "Grade A Tray (30 eggs/tray)". Only the per-egg unit
                      // is bare — by identity, not factor, so "1 egg/dozen"
                      // still shows (see eggsPerUnit above).
                      const f = p.defaultUnit === "Egg" ? null : eggsPerUnit(p.defaultUnit);
                      return (
                        <option key={p.id} value={p.id}>
                          {f !== null
                            ? t("productOptionWithUnit", { name: p.name, count: f, unit: unitWord(p.defaultUnit) })
                            : p.name}
                        </option>
                      );
                    })}
                  </select>
                </label>
                <label>{t("perLabel")}
                  <select value={unit} onChange={(e) => setUnit(e.target.value)}>
                    {SELLING_UNITS.map((u) =>
                      <option key={u} value={u}>{t(`unit${u}`)}</option>)}
                  </select>
                </label>
                {/* Sibling label, not wrapping: a <label> may not contain
                    interactive content other than its own control, and the
                    stepper carries two buttons. */}
                <div className="numfield-field">
                  {/* #445 — the label names the unit ("Quantity (trays)" not
                      bare "Quantity"), and the live hint shows the resulting
                      egg count while typing, so "2 trays" typed as 60 is
                      visibly 1,800 eggs before Add line is pressed. */}
                  <label htmlFor={addQtyId}>{t("quantityWithUnit", { unit: unitWord(unit) })}</label>
                  <NumberField id={addQtyId} label={t("quantityWithUnit", { unit: unitWord(unit) }).toLowerCase()}
                    value={qty} onChange={setQty} min={1} />
                  {(() => {
                    // Per-egg suppressed by identity, not factor (see above).
                    const f = unit === "Egg" ? null : eggsPerUnit(unit);
                    return f !== null
                      ? <p className="muted">{t("equalsEggs", { count: qty * f })}</p>
                      : null;
                  })()}
                </div>
                <label>{t("unitPriceWithCurrency", { code: active.currencyCode })}
                  <input type="number" min={0} step={10 ** -active.currencyMinorUnit} value={price}
                    onChange={(e) => setPrice(e.target.value)} />
                </label>
                <BusyButton disabled={busy || !productId} busy={isPending("add-item")}
                  onClick={onAddItem}>{t("addLine")}</BusyButton>
              </div>
              {/* #720 R11 — AFTER .form-grid, not a grid cell: a third child in
                  a cell bottom-aligns under .form-grid's align-items:end and
                  lifts that field's label/input above the row (R8, then R9's
                  position:absolute chased the same shape into an overlap at
                  420px in tl). In normal flow after the grid it wraps to any
                  height in any locale with nothing to overlap. Knowing trade:
                  it renders left-aligned under the whole form rather than
                  under the Unit price input, which diverges from the artboard
                  — the price of a layout that cannot overlap in any locale. */}
              {(() => {
                // #720 — the same amount AddOrderItemHandler would snapshot
                // as ListUnitPriceMinorUnits if Add line were pressed now.
                const list = products.find((p) => p.id === productId)?.defaultPriceMinorUnits ?? null;
                if (list === null) return null;
                const typed = parseMoneyToMinorUnits(price, active.currencyMinorUnit);
                if (!Number.isFinite(typed) || typed === list) return null;
                const perUnit = Math.abs(typed - list);
                const amount = fmt.money(perUnit, active.currencyCode, active.currencyMinorUnit);
                // A zero list price is legal (Product.cs rejects only
                // negatives) — dividing by it for a percent would be NaN/Infinity.
                // <input min={0}> is a validation constraint, not an input
                // filter, so a negative typed price against a zero list can
                // still reach the below branch here.
                if (typed < list) {
                  return list === 0
                    ? <p className="discount">{t("listPriceHintBelowNoPct", { amount })}</p>
                    : <p className="discount">
                        {t("listPriceHintBelow", { amount, percent: discountPercent((perUnit * 100) / list) })}
                      </p>;
                }
                return list === 0
                  ? <p className="discount">{t("listPriceHintAboveNoPct", { amount })}</p>
                  : <p className="discount">
                      {t("listPriceHintAbove", { amount, percent: discountPercent((perUnit * 100) / list) })}
                    </p>;
              })()}
              <div className="actions">
                <BusyButton disabled={busy || active.items.length === 0}
                  busy={isPending(`confirm:${active.id}`)} onClick={() => void onConfirm()}>
                  {t("confirmOrderButton")}
                </BusyButton>
                <BusyButton className="link" disabled={busy} busy={isPending(`cancel:${active.id}`)}
                  onClick={() => void onCancel()}>{t("cancelDraft")}</BusyButton>
                <button className="link" onClick={closeOrderPanel}>{t("close")}</button>
              </div>
            </>
          )}
          {active.status === "Confirmed" && canSettle && payments && (
            <>
              <h4>{t("payments")}</h4>
              {payments.items.length > 0 && (
                <table className="data">
                  <thead>
                    <tr><th>{t("date")}</th><th className="num">{t("amount")}</th><th>{t("method")}</th><th>{t("reference")}</th><th></th></tr>
                  </thead>
                  <tbody>
                    {payments.items.map((p) => (
                      <tr key={p.id} className={p.voided ? "inactive" : undefined}
                        title={p.note ?? undefined}>
                        <td className="nowrap"><FarmDate iso={p.paymentDate} /></td>
                        <td className="num">{fmt.money(p.amountMinorUnits, p.currencyCode, p.currencyMinorUnit)}</td>
                        <td>{t(`method${p.method as PaymentMethod}`)}</td>
                        <td className="nowrap">{p.referenceNumber ?? "—"}</td>
                        <td>
                          {p.voided
                            ? <span className="badge badge-danger" title={p.voidReason ?? undefined}>{statusLabel("Voided")}</span>
                            : isAdmin ? (
                              <BusyButton className="link" disabled={busy} busy={isPending(`void-payment:${p.id}`)}
                                onClick={() => void onVoidPayment(p.id, p.version)}>{t("voidPaymentButton")}</BusyButton>
                            ) : null}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              <p>
                <Trans
                  ns="sales"
                  i18nKey="paymentsSummary"
                  values={{
                    paid: fmt.money(payments.paidMinorUnits, payments.currencyCode, payments.currencyMinorUnit),
                    outstanding: fmt.money(payments.outstandingMinorUnits, payments.currencyCode, payments.currencyMinorUnit),
                  }}
                  components={{ strong: <strong /> }}
                />
              </p>
              {payments.outstandingMinorUnits > 0 && (
                <div className="panel-actions">
                  <button type="button" onClick={() => {
                    setPayDate(today);
                    // A new session starts clean: an earlier attempt that
                    // succeeded after being abandoned leaves its amount in
                    // place otherwise, ready to be sent again under a fresh
                    // key — a duplicate payment (codex review).
                    setPayAmount("");
                    setPayRef("");
                    setPayNote("");
                    // The method too (CodeRabbit): it is as much part of the
                    // abandoned attempt as the amount, and leaving it behind
                    // preselects a method this payment was never given.
                    setPayMethod(DEFAULT_PAY_METHOD);
                    openDialog("record-payment"); // a new session — see #477
                    setPaying(true);
                  }}>
                    {t("recordPayment")}
                  </button>
                </div>
              )}

              <Dialog open={paying} title={t("recordPayment")} onClose={closePayment}>
                <div className="form-grid">
                  <label>{t("date")}
                    <input type="date" value={payDate} max={today}
                      onChange={(e) => setPayDate(e.target.value)} />
                  </label>
                  <label>{t("amountWithCurrency", { code: payments.currencyCode })}
                    <input type="number"
                      min={(1 / 10 ** payments.currencyMinorUnit).toFixed(payments.currencyMinorUnit)}
                      step="any" value={payAmount}
                      onChange={(e) => setPayAmount(e.target.value)} />
                  </label>
                  <label>{t("method")}
                    <select value={payMethod} onChange={(e) => setPayMethod(e.target.value)}>
                      {(["Cash", "Check", "Card", "BankTransfer", "MobilePayment", "Other"] as const).map((m) => (
                        <option key={m} value={m}>{t(`method${m}`)}</option>
                      ))}
                    </select>
                  </label>
                  <label>{t("referenceOptional")}
                    <input value={payRef} maxLength={50}
                      onChange={(e) => setPayRef(e.target.value)} />
                  </label>
                  <label>{t("noteOptional")}
                    <input value={payNote} maxLength={500}
                      onChange={(e) => setPayNote(e.target.value)} />
                  </label>
                  {/* #474 — this dialog's own write only: see the new-order
                      dialog above. A void raised from the payments table can
                      land while this is open, and is not this form's failure. */}
                  <DialogError errors={errors} scope="record-payment" />
                  <div className="dialog-foot">
                    <button type="button" className="link" onClick={closePayment}>{tc("cancel")}</button>
                    <BusyButton disabled={busy || !payAmount} busy={isPending("record-payment")}
                      onClick={onRecordPayment}>
                      {t("recordPayment")}
                    </BusyButton>
                  </div>
                </div>
              </Dialog>
            </>
          )}
          {active.status === "Voided" && active.voidReason && (
            <p className="muted">{t("voidReasonLabel", { reason: active.voidReason })}</p>
          )}
          {active.status !== "Draft" && (
            <div className="actions">
              {active.status === "Confirmed" && isAdmin && (
                <BusyButton className="link" disabled={busy} busy={isPending(`void:${active.id}`)}
                  onClick={() => void onVoid()}>
                  {t("voidOrderButton")}
                </BusyButton>
              )}
              {active.status === "Confirmed" && !isAdmin && (
                <span className="muted">{t("voidingNeedsAdmin")}</span>
              )}
              <button className="link" onClick={closeOrderPanel}>{t("close")}</button>
            </div>
          )}
        </div>
      )}

      {/* The page's own copy, for everything not behind a dialog — and for a
          failure that is nobody's dialog even while one is open, rather than
          swallowing it. Unconditional: a dialog's message lives in a slot only
          that dialog reads, so there is nothing here to double up on (#474). */}
      {errors.page && <p className="error">{errors.page}</p>}
      {message && <p className="success">{message}</p>}

      <h3>{t("ordersHeading")}</h3>
      <div className="form-grid">
        <label>{t("status")}
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">{t("allOption")}</option>
            <option value="Draft">{statusLabel("Draft")}</option>
            <option value="Confirmed">{statusLabel("Confirmed")}</option>
            <option value="Cancelled">{statusLabel("Cancelled")}</option>
            <option value="Voided">{statusLabel("Voided")}</option>
          </select>
        </label>
        <div className="filter-customer">
          {/* #512 US5 (T057) — `customerId` in the URL is the sole source of
              truth (FR-046); select/clear clone the CURRENT URLSearchParams
              and touch only `customerId` (FR-047), preserving every unrelated
              key (`status` included, once it moves to the URL — none does
              today, but the clone-and-set pattern costs nothing to get right
              now). Malformed values never reach here (`requestedId` is ""),
              so the picker shows blank/All for them, never an unavailable
              state — a malformed id is absent, not inaccessible (FR-048). */}
          <CustomerPicker
            label={t("customer")}
            required={false}
            open={customerFilterPickerOpen}
            requestedId={customerFilter || null}
            onSnapshot={(snap) => {
              setCustomerFilterSnapshot(snap);
              if (snap.committed) setCustomerFilterEntity(snap.committed);
            }}
            onCommit={(c) => {
              const next = new URLSearchParams(searchParams);
              next.set("customerId", c.id);
              setCustomerFilterEntity(c);
              setSearchParams(next);
              setCustomerFilterPickerOpen(false);
            }}
            onClear={() => {
              const next = new URLSearchParams(searchParams);
              next.delete("customerId");
              setCustomerFilterEntity(null);
              setSearchParams(next);
            }}
            onEscape={() => setCustomerFilterPickerOpen(false)}
            onOutsideClick={() => setCustomerFilterPickerOpen(false)}
            trigger={
              <button type="button" className="named-picker-trigger"
                onClick={() => setCustomerFilterPickerOpen(true)}>
                {customerFilter === ""
                  ? t("allOption")
                  : customerFilterEntity?.name
                    ?? (customerFilterSnapshot.selectionPhase === "unavailable"
                      ? t("filterCustomerUnavailable")
                      : i18n.t("namedEntityPicker:loading"))}
              </button>
            }
          />
          {/* FR-049 — an unavailable filter identity must offer Clear even
              while the picker is closed (the generic engine's own Clear only
              renders inside the open combobox, and only once something is
              actually committed — never during `unavailable`, where nothing
              is). This is the page-owned affordance that closes the gap. */}
          {customerFilterSnapshot.selectionPhase === "unavailable" && (
            <button type="button" className="link" onClick={() => {
              const next = new URLSearchParams(searchParams);
              next.delete("customerId");
              setCustomerFilterEntity(null);
              setSearchParams(next);
            }}>
              {i18n.t("namedEntityPicker:clear")}
            </button>
          )}
        </div>
      </div>
      {/* The list's own failure, beside the workspace rather than instead of
          it — and self-healing on the next successful load (#469). */}
      {orders.error && <p className="error" role="alert">{orders.error}</p>}
      {/* One window's orders must never sit under another window's filters,
          not even for the length of the request (#469). FR-050: `customerFilterStale`
          extends the hide to cover the render(s) between a URL identity change
          and `reloading` taking over — see its declaration above. */}
      {(orders.reloading || customerFilterStale) ? (
        <p className="muted">{t("loading")}</p>
      ) : orders.rows.length === 0 ? (
        hasActiveFilter
          ? <EmptyState icon={FilterX} message={t("noOrdersMatch")}
              action={{
                label: tc("clearFiltersButton"),
                onClick: () => {
                  const next = new URLSearchParams(searchParams);
                  next.delete("customerId");
                  setCustomerFilterEntity(null);
                  setStatusFilter("");
                  setSearchParams(next);
                },
              }} />
          // Same condition AND same handler as the page-head New order button
          // above — a customer-less farm gets the sentence alone there too.
          : <EmptyState icon={ShoppingCart} message={t("noOrdersMessage")}
              action={customers.length > 0 ? {
                label: t("newOrder"),
                onClick: () => {
                  setOrderDate(today);
                  setNewOrderCustomerPickerOpen(true);
                  openDialog("create-order"); // a new session — see #477
                  setCreatingOrder(true);
                },
              } : undefined} />
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("reference")}</th><th>{t("date")}</th><th>{t("customer")}</th><th>{t("status")}<GlossaryLink term="ConfirmOrder" /></th><th className="num">{t("discount")}</th><th className="num">{t("total")}</th><th>{tc("recordHistoryHeader")}</th><th></th></tr>
            </thead>
            <tbody>
              {orders.rows.map((o) => (
                <tr key={o.id}>
                  <td className="nowrap">{o.referenceNumber}</td>
                  <td className="nowrap"><FarmDate iso={o.orderDate} /></td>
                  <td>{rowCustomerName(o)}</td>
                  <td><StatusBadge status={o.status} label={statusLabel(o.status)} /></td>
                  {/* #724 — one column on the only per-order list in the app.
                      class="num" per #650: styles.num.test.ts pins td.num to
                      right-aligned tabular figures that never wrap. */}
                  <td className="num">{(() => {
                    const d = orderDiscount(o.items);
                    if (d.kind === "unknown") return <span className="muted discount-note">{t("discountUnknown")}</span>;
                    // Round 1 — the em dash means "sold at list". An order only
                    // part of which is measurable must not borrow that glyph.
                    if (d.kind !== "below") {
                      return d.partial
                        ? <span className="muted discount-note">{t("discountPartialNote")}</span>
                        : "—";
                    }
                    const amount = fmt.money(d.amountMinorUnits, o.currencyCode, o.currencyMinorUnit);
                    return (
                      <>
                        <span className="badge badge-warn">
                          {d.percent === null
                            ? t("discountBadgeNoPct", { amount })
                            : t("discountBadge", { amount, percent: discountPercent(d.percent) })}
                        </span>
                        {d.partial ? <><br /><span className="muted discount-note">{t("discountPartialNote")}</span></> : null}
                      </>
                    );
                  })()}
                  {/* Outside the branch above, deliberately: the reason is a
                      stored fact about the order, not a property of what the
                      measurement currently says about its lines. */}
                  {o.discountReasonCode && (
                    <><br /><span className="muted discount-note" data-testid="row-discount-reason">
                      {discountReasonLabel(o.discountReasonCode)}
                    </span></>
                  )}</td>
                  <td className="num">{fmt.money(o.totalMinorUnits, o.currencyCode, o.currencyMinorUnit)}</td>
                  <ProvenanceCell history={o} official="confirmed" />
                  <td>
                    <button className="link" disabled={busy} onClick={() => onOpen(o.id)}>{t("open")}</button>
                    {/* #493 — full audit trail for this record, distinct from
                        the created/last-changed summary in ProvenanceCell.
                        Admin-gated: /api/v1/audit is AdminOnly, so the Sales
                        role (which can settle orders here) would otherwise
                        hit a 403 (codex review of #516). */}
                    {isAdmin && (
                      <Link className="link" to={`/audit?entityId=${o.id}`}>
                        {tc("recordHistory.viewHistoryLink")}
                      </Link>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {orders.canLoadMore && (
            // A guarded READ — the hook withdraws this control for the
            // duration of any load, so it cannot mix two windows (#469).
            <button className="link" disabled={busy}
              onClick={() => void orders.loadMore()}>{t("loadMore")}</button>
          )}
        </>
      )}

      {confirmDialog}
    </section>
  );
}
