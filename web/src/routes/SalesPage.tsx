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
import type { Customer, EggUnitConversion, OrderPayments, Product, SalesOrder } from "../api/cluckwork";
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
import { useDialogErrors } from "../components/useDialogErrors";
import { useConfirm } from "../components/useConfirm";
import { usePagedList } from "../components/usePagedList";
import { usePendingAction } from "../components/usePendingAction";
import { StatusBadge } from "../components/StatusBadge";
import { GlossaryLink } from "../components/GlossaryLink";
import { newId } from "../lib/ids";
import { useFarm, useFarmToday } from "../farm/useFarm";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

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

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

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

// #23 + #24 (orders half): create a draft order, add/edit/remove graded lines,
// confirm (FIFO allocation), cancel drafts, browse/filter the order list.
export function SalesPage() {
  const { t } = useTranslation("sales");
  const fmt = useFormat();
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
  const { confirm, askReason, confirmDialog } = useConfirm();
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
  const [active, setActive] = useState<SalesOrder | null>(null);
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

  // per-row edit state (draft orders)
  const [editItemId, setEditItemId] = useState<string | null>(null);
  const [editQty, setEditQty] = useState(1);
  const [editPrice, setEditPrice] = useState("0");

  // #236 — the shared flight guard (the hook's internal ref replaced the old
  // inFlight ref here). `busy` inerts every trigger; isPending(scope) spins
  // exactly the clicked one, so a row with two verbs never lies about which
  // is working.
  const { busy, isPending, run: runPending } = usePendingAction();
  // Idempotency keys bound to (action, target) and rotated ONLY after the whole
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

  // One slot per PLACE a message can appear: the page, and each dialog by
  // scope. Sales learned every rule of this the hard way over four review
  // rounds (#474 → #477 → #480 → #481); #479 extracted the result into
  // useDialogErrors once the same one-shared-slot shape turned up on ten other
  // screens. The rules and the incidents that earned them live with the hook —
  // Sales-specific is only WHICH scopes own a dialog.
  const errors = useDialogErrors();
  // Pulled out for the payments effect's dependency list: both are stable, and
  // naming them is what lets that effect declare its real dependencies.
  const { abandon: abandonError, setPage: setPageError } = errors;
  // The scopes that own a dialog. run() routes a failure by this and nothing
  // else, so a new dialog action is one entry, not a new render condition.
  const DIALOG_SCOPES = ["create-order", "record-payment"];
  const [message, setMessage] = useState<string | null>(null);

  // Payments (#89, admin-only money data) — settlement state of the open
  // confirmed order.
  const [payments, setPayments] = useState<OrderPayments | null>(null);
  const [payDate, setPayDate] = useState(today);
  const [payAmount, setPayAmount] = useState("");
  const [payMethod, setPayMethod] = useState("Cash");
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
        const saleableGrades = new Set(g.filter((x) => x.isSaleable).map((x) => x.id));
        const sellable = p.filter(
          (x) => x.active && x.eggGradeId !== null && saleableGrades.has(x.eggGradeId));
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
    // `abandon`, not `clearDialog`: a payment write can still be out when this
    // runs, so the slot is emptied and that attempt muted together. Clearing
    // alone would let the rejection settle into the slot afterwards, to be
    // found by whoever opens a payment form next.
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
    abandonError("record-payment");
    if (activeId === null || activeStatus !== "Confirmed" || !canSettle) return;
    let cancelled = false;
    listOrderPayments(activeId)
      .then((p) => { if (!cancelled) setPayments(p); })
      .catch(() => {
        if (!cancelled) setPageError(i18n.t("sales:loadPaymentsFailed"));
      });
    return () => { cancelled = true; };
    // The two hook members this effect uses are destructured above and listed
    // here, rather than depending on `errors` — that object is rebuilt every
    // render, so naming it would re-run this on every render and re-fetch the
    // payments. There is no eslint in this package to have caught either.
  }, [activeId, activeStatus, canSettle, abandonError, setPageError]);

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

  // Rebased on usePendingAction (#236), gaining the scope parameter the old
  // scopeless helper lacked. Pending scopes are independent of the idempotency
  // key scopes built inside each action (nothing couples the two). The helper
  // also wraps two READS — "open:<id>" and "more" — which keep the flight
  // guard but deliberately get no BusyButton treatment (#236 is writes).
  const run = (scope: string, fn: () => Promise<void>) =>
    runPending(scope, async () => {
      // The slot this attempt owns — its dialog's, or the page's. Everything
      // that is not a dialog's belongs to the page, so one lookup decides both
      // where the attempt clears and where its verdict lands.
      const slot = DIALOG_SCOPES.includes(scope) ? scope : null;
      // Its own slot only, and un-muted: a dialog write must not wipe a page
      // failure the user has not seen, and abandoning one attempt must not
      // mute the next.
      errors.beginAttempt(slot);
      setMessage(null);
      try {
        await fn();
      } catch (err) {
        // Dropped outright if the user gave up on this one — Cancel stays live
        // during `busy`, and Escape and the backdrop dismiss too (#474, and
        // the codex + pi review of #476). `report` owns that decision.
        errors.report(slot, errText(err));
      }
    });

  // #474 — dismissing empties the dialog's slot, so reopening the form shows no
  // stale verdict, and mutes the attempt still out, so its failure is not
  // reported against the session the user opens next. Both live in `abandon`.
  const dismiss = (scope: string, setOpen: (open: boolean) => void) => {
    setOpen(false);
    errors.abandon(scope);
  };
  const closeNewOrder = () => {
    setNewOrderCustomerPickerOpen(false);
    dismiss("create-order", setCreatingOrder);
  };
  const closePayment = () => dismiss("record-payment", setPaying);

  const onCreateOrder = () => run("create-order", async () => {
    // #512 (T039) — the handler's own guard: canSubmit is the write-safety
    // boundary (a disabled button alone is not). An exploring/uninitialized
    // or unavailable picker must not ship a stale committed id.
    if (!customer || !customerSnapshot.canSubmit) return;
    // runWrite claims the list ticket before the POST, so a filter change
    // made while it is in flight keeps the view (#469).
    await orders.runWrite(async () => {
      const created = await createOrder({ customerId: customer.id, orderDate }, keyFor("create-order"));
      setActive(await getOrder(created.id));
    });
    clearKey("create-order");
    setNewOrderCustomerPickerOpen(false);
    setCreatingOrder(false); // only on success — a throw keeps the dialog up
  });

  const onAddItem = () => run("add-item", async () => {
    if (!active) return;
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
    const scope = `add-item:${active.id}`;
    // #445 — bind the previewed factor to the write: if an admin redefined
    // the unit after this page read its conversions, the server refuses
    // (SalesOrder.UnitDefinitionChanged) instead of recording a QuantityBase
    // different from the "= N eggs" the seller saw. undefined when nothing
    // was previewed (per-egg unit, or no/failed conversions read).
    const previewed = unit === "Egg" ? null : eggsPerUnit(unit);
    try {
      await addOrderItem(active.id,
        {
          productId, quantity: qty, unit, unitPriceMinorUnits: minorUnits,
          expectedEggsPerUnit: previewed ?? undefined,
        },
        keyFor(scope));
    } catch (err) {
      // Any server rejection may mean the conversions moved under us (the
      // UnitDefinitionChanged case) — refresh them so the preview and the
      // next attempt use the current factors instead of looping on stale
      // ones. Fire-and-forget: the thrown error still surfaces normally.
      if (err instanceof ApiError) listEggUnitConversions().then(setConversions).catch(() => {});
      throw err;
    }
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  const onUpdateItem = (itemId: string) => run(`update-item:${itemId}`, async () => {
    if (!active) return;
    // #398 — same whole-number guard as the add-line control, above.
    if (!Number.isInteger(editQty)) throw new Error(i18n.t("sales:quantityMustBeWholeNumber"));
    const minorUnits = parseMoneyToMinorUnits(editPrice, active.currencyMinorUnit);
    if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error(i18n.t("sales:invalidUnitPrice"));
    const scope = `update-item:${itemId}`;
    await updateOrderItem(active.id, itemId,
      { quantity: editQty, unitPriceMinorUnits: minorUnits }, keyFor(scope));
    setEditItemId(null);
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  const onRemoveItem = (itemId: string) => run(`remove-item:${itemId}`, async () => {
    if (!active) return;
    const scope = `remove-item:${itemId}`;
    await removeOrderItem(active.id, itemId, keyFor(scope));
    setActive(await getOrder(active.id));
    clearKey(scope);
  });

  // One-way actions (#59). Confirm BEFORE run() so buttons don't flash
  // disabled while the user decides.
  const onConfirm = async () => {
    const ok = await confirm({
      title: i18n.t("sales:confirmOrderTitle"),
      body: i18n.t("sales:confirmOrderBody"),
      confirmLabel: i18n.t("sales:confirmOrderConfirmLabel"),
    });
    if (!ok || !active) return;
    void run(`confirm:${active.id}`, async () => {
      const scope = `confirm:${active.id}`;
      await orders.runWrite(async () => {
        await confirmOrder(active.id, keyFor(scope));
        const refreshed = await getOrder(active.id);
        setActive(refreshed);
        setMessage(i18n.t("sales:orderConfirmed", { ref: refreshed.referenceNumber }));
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
    void run(`cancel:${active.id}`, async () => {
      const scope = `cancel:${active.id}`;
      await orders.runWrite(async () => {
        await cancelOrder(active.id, keyFor(scope));
        setActive(null);
        setMessage(i18n.t("sales:draftOrderCancelled"));
      });
      clearKey(scope);
    });
  };

  // Undo of a mistaken confirm (#60). Reason prompt doubles as the confirm
  // dialog, hoisted above run() like the other one-way actions; cancelling the
  // prompt aborts the void.
  const refreshPayments = async (orderId: string) =>
    setPayments(await listOrderPayments(orderId));

  const onRecordPayment = () => void run("record-payment", async () => {
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
    setPayAmount("");
    setPayRef("");
    setPayNote("");
    await refreshPayments(active.id);
    setMessage(i18n.t("sales:paymentRecorded"));
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
      await refreshPayments(active.id);
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
    void run(`void:${active.id}`, async () => {
      const scope = `void:${active.id}`;
      await orders.runWrite(async () => {
        await voidOrder(active.id, reason, keyFor(scope));
        const refreshed = await getOrder(active.id);
        setActive(refreshed);
        setMessage(i18n.t("sales:orderVoided", { ref: refreshed.referenceNumber }));
      });
      clearKey(scope);
    });
  };

  // Always fetch fresh on open — the list row may be stale relative to
  // mutations made through the panel since the list was loaded.
  const onOpen = (id: string) => run(`open:${id}`, async () => {
    setActive(await getOrder(id));
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
        {customers.length > 0 && !(orders.rows.length === 0 && !customerFilter) && (
          // Re-seeded on open, not only at mount: a tab left open across
          // farm-midnight would otherwise offer yesterday as the order date
          // while the picker's own ceiling had already moved on (codex review
          // of #123).
          <button type="button" onClick={() => {
            // Nothing to clear on the way in: #479 moved that onto the
            // dismissal, so the slot is already empty before a reopen.
            setOrderDate(today);
            setNewOrderCustomerPickerOpen(true);
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
              <thead><tr><th>{t("product")}</th><th className="num">{t("qty")}</th><th className="num">{t("eggs")}</th><th className="num">{t("unitPrice")}</th><th className="num">{t("lineTotal")}</th><th></th></tr></thead>
              <tbody>
                {active.items.map((i) => (
                  <tr key={i.id}>
                    <td>{productName(i.productId)}{" "}
                      <span className="muted">{t("perUnit", { unit: i.unit.toLowerCase() })}
                        {i.baseUnitFactor > 1 ? ` ${t("eggsCount", { count: i.baseUnitFactor })}` : ""}</span></td>
                    {editItemId === i.id ? (
                      <>
                        <td className="num">
                          {/* No visible label in the cell — the sr-only one names
                              the input; the buttons carry their own names. */}
                          <label className="sr-only" htmlFor={editQtyId}>{t("editQuantityAriaLabel")}</label>
                          <NumberField id={editQtyId} label={t("editQuantityAriaLabel").toLowerCase()}
                            value={editQty} onChange={setEditQty} min={1} />
                        </td>
                        {/* #445 — live: the eggs column tracks the edited
                            quantity instead of going blank, so a unit/count
                            mix-up is visible mid-edit too. */}
                        <td className="num muted">{fmt.count(i.baseUnitFactor * editQty)}</td>
                        <td className="num"><input className="cell" type="number" min={0}
                          aria-label={t("editUnitPriceAriaLabel")}
                          step={10 ** -active.currencyMinorUnit} value={editPrice}
                          onChange={(e) => setEditPrice(e.target.value)} /></td>
                        <td className="num">—</td>
                        <td>
                          <BusyButton className="link" disabled={busy} busy={isPending(`update-item:${i.id}`)}
                            onClick={() => onUpdateItem(i.id)}>{t("save")}</BusyButton>
                          <button className="link" onClick={() => setEditItemId(null)}>{t("cancelEdit")}</button>
                        </td>
                      </>
                    ) : (
                      <>
                        <td className="num">{fmt.count(i.quantity)}</td>
                        <td className="num">{fmt.count(i.quantityBase)}</td>
                        <td className="num">{fmt.money(i.unitPriceMinorUnits, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td className="num">{fmt.money(i.unitPriceMinorUnits * i.quantity, i.currencyCode, i.currencyMinorUnit)}</td>
                        <td>
                          {active.status === "Draft" && (
                            <>
                              <button className="link" disabled={busy} onClick={() => {
                                setEditItemId(i.id);
                                setEditQty(i.quantity);
                                // The ORDER's scale, not the line's own
                                // snapshot: the edit is submitted at the
                                // order's, and reading the row's would be the
                                // same two-scales bug one field over.
                                setEditPrice(priceInput(i.unitPriceMinorUnits, priceScale));
                              }}>{t("edit")}</button>
                              <BusyButton className="link" disabled={busy} busy={isPending(`remove-item:${i.id}`)}
                                onClick={() => onRemoveItem(i.id)}>{t("remove")}</BusyButton>
                            </>
                          )}
                        </td>
                      </>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
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
              <div className="actions">
                <BusyButton disabled={busy || active.items.length === 0}
                  busy={isPending(`confirm:${active.id}`)} onClick={() => void onConfirm()}>
                  {t("confirmOrderButton")}
                </BusyButton>
                <BusyButton className="link" disabled={busy} busy={isPending(`cancel:${active.id}`)}
                  onClick={() => void onCancel()}>{t("cancelDraft")}</BusyButton>
                <button className="link" onClick={() => setActive(null)}>{t("close")}</button>
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
                    setPayDate(today); setPaying(true);
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
              <button className="link" onClick={() => setActive(null)}>{t("close")}</button>
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
        customerFilter
          ? <EmptyState icon={FilterX} message={t("noOrdersMatch")}
              action={{
                label: tc("clearFiltersButton"),
                onClick: () => {
                  const next = new URLSearchParams(searchParams);
                  next.delete("customerId");
                  setCustomerFilterEntity(null);
                  setSearchParams(next);
                },
              }} />
          // Same condition AND same handler as the page-head New order button
          // above — a customer-less farm gets the sentence alone there too.
          : <EmptyState icon={ShoppingCart} message={t("noOrdersMatch")}
              action={customers.length > 0 ? {
                label: t("newOrder"),
                onClick: () => {
                  setOrderDate(today);
                  setNewOrderCustomerPickerOpen(true);
                  setCreatingOrder(true);
                },
              } : undefined} />
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("reference")}</th><th>{t("date")}</th><th>{t("customer")}</th><th>{t("status")}<GlossaryLink term="ConfirmOrder" /></th><th className="num">{t("total")}</th><th>{tc("recordHistoryHeader")}</th><th></th></tr>
            </thead>
            <tbody>
              {orders.rows.map((o) => (
                <tr key={o.id}>
                  <td className="nowrap">{o.referenceNumber}</td>
                  <td className="nowrap"><FarmDate iso={o.orderDate} /></td>
                  <td>{rowCustomerName(o)}</td>
                  <td><StatusBadge status={o.status} label={statusLabel(o.status)} /></td>
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
