import { useCallback, useEffect, useEffectEvent, useId, useRef, useState } from "react";
import { FilterX, Plus, ShoppingCart } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Box, Button, Stack, Typography, useMediaQuery } from "@mui/material";
import { listCustomers, listEggGrades, listEggUnitConversions, listOrders, listProducts } from "../../api/cluckwork";
import type { Customer, EggGrade, EggUnitConversion, Product } from "../../api/cluckwork";
import { useFormat } from "../../farm/useFormat";
import { useAuth } from "../../auth/useAuth";
import { FieldConsole, ConsoleSubhead, ConsoleSummary, CONSOLE_LINK_SX, CONSOLE_PANEL_SX, CONSOLE_SPLIT_SX } from "../../components/FieldConsole";
import { Dialog } from "../../components/Dialog";
import { EmptyState } from "../../components/EmptyState";
import { focusPhoneRow } from "../../components/PhoneLedger";
import { useDialogAction } from "../../components/useDialogAction";
import { useConfirm } from "../../components/useConfirm";
import { usePagedList } from "../../components/usePagedList";
import { MD_UP_QUERY } from "../../lib/breakpoints";
import { discountCeiling } from "../../lib/discountCeiling";
import { useFarm, useFarmToday } from "../../farm/useFarm";
import i18n from "../../i18n";
import { statusLabel } from "../../i18n/enums";
import { AddLineForm, useAddLineFields } from "./AddLineForm";
import { NewOrderDialog } from "./NewOrderDialog";
import { OrderDetailsDialog, OrderList } from "./OrderList";
import { OrderFilters } from "./OrderFilters";
import { OrderLines } from "./OrderLines";
import { PaymentsPanel } from "./PaymentsPanel";
import { SettlementRail } from "./SettlementRail";
import { isSellingUnit, priceInput, sellableProducts } from "./orderMath";
import { CHIP_SX, OrderStatus } from "./salesUi";
import { useActiveOrder } from "./useActiveOrder";
import { useIdempotencyKeys } from "./useIdempotencyKeys";
import { useNewOrder } from "./useNewOrder";
import { useOrderCommands } from "./useOrderCommands";
import { useOrderFilters } from "./useOrderFilters";
import { usePayments } from "./usePayments";

const PAGE = 50;

// #23 + #24 (orders half): create a draft order, add/edit/remove graded lines,
// confirm (FIFO allocation), cancel drafts, browse/filter the order list.
export function SalesPage() {
  const { t } = useTranslation("sales");
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  // #987 — the phone list's three surfaces: the filter dialog the chips open,
  // the order row's read-only peek, and the payment row's.
  // A peek holds an ID, never a row: the row it describes is re-read from the
  // live list every render, so a refresh can neither leave it showing a stale
  // status nor leave it offering an action the record no longer allows (#988
  // review r1).
  const [filterOpen, setFilterOpen] = useState(false);
  const [detailsId, setDetailsId] = useState<string | null>(null);
  // #988 review r2 — a peek action closes its dialog and disables its row in
  // the same commit, so `Dialog`'s restore (one retry a frame later) lands on
  // <body> for any request slower than a frame. The page names the target
  // itself: the workspace the action opened, or the row once it is live again.
  const orderPanelHeadingRef = useRef<HTMLHeadingElement>(null);
  const focusAfterWrite = useRef<string | null>(null);
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const { farm } = useFarm();
  // #727 — the ceiling THIS caller is bound by, or null. Null already covers
  // both "the farm sets none" and "you may exceed it", so there is no role
  // check here; the server collapsed them deliberately.
  const ceiling = discountCeiling(farm?.yourMaxDiscountPercent ?? null);
  // No fraction digits, because Farm settings only offers whole percents. Still
  // fmt.count rather than String(): the day it offers 12.5, the decimal
  // separator has to be the farm's — es writes 12,5.
  const ceilingPercent = ceiling === null ? "" : fmt.count(ceiling.percent);
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

  const listFilter = useOrderFilters(canSettle);
  const {
    statusFilter, customerFilter, unpaidFilter, hasActiveFilter,
    customerFilterStale, setCustomerFilterStale, customerFilterName, clearFilters,
  } = listFilter;
  const activeOrder = useActiveOrder();
  const { active, setActive } = activeOrder;
  const addLine = useAddLineFields();
  const { setProductId, setUnit, setPrice } = addLine;
  // NumberField owns its own input, so labels point at it by id (F134 idiom).
  const fieldId = useId();
  const addQtyId = `${fieldId}-qty`;
  const editQtyId = `${fieldId}-edit-qty`;
  const orderPanelHeadingId = `${fieldId}-order-panel-heading`;

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

  const { keyFor, clearKey } = useIdempotencyKeys();

  const [message, setMessage] = useState<string | null>(null);
  // #703 — the flight guard (#236), the per-place message slots (#479) and the
  // dialog-session generation (#477 part 2) are composed by one shared hook.
  // Sales learned every rule of this the hard way over five review rounds
  // (#474 → #477 → #479 → #625 → #702); the rules and the incidents that
  // earned them live with the hook. Sales-specific is only WHICH scopes own a
  // dialog, and that the page-level success message clears on each attempt.
  const action = useDialogAction(
    ["create-order", "record-payment", "order-panel"],
    { onAttempt: () => setMessage(null) },
  );
  const { busy, errors, dismissDialog } = action;
  const newOrder = useNewOrder(today, action);
  const { setCustomer, setCustomerGen, openNewOrder } = newOrder;

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
        unpaid: unpaidFilter || undefined,
        limit,
        offset,
      }),
      [statusFilter, customerFilter, unpaidFilter],
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
  }, [customerFilterStale, orders.reloading, setCustomerFilterStale]);

  // An effect event, so the price prefill reads the farm's scale when the setup
  // read lands. A mount-time capture missed a farm that arrived in between (a
  // cold load renders before /account answers) and left the price blank.
  const applySetupRead = useEffectEvent((c: Customer[], p: Product[], g: EggGrade[]) => {
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
  });

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
      .then(([c, p, g]) => applySetupRead(c, p, g))
      .catch(() => setSetupError(i18n.t("sales:loadSalesDataFailed")));
    // Separate from the Promise.all above ON PURPOSE: the conversions only
    // feed supplementary display (the live "= N eggs" hint and the option
    // annotations), so a failed read degrades to the pre-#445 labels instead
    // of blocking the whole screen. A genuinely missing conversion still 422s
    // server-side at add time (SalesOrder.NoUnitConversion).
    listEggUnitConversions().then(setConversions).catch(() => {});
  }, []);

  const pay = usePayments({
    active, activeIdRef: activeOrder.activeIdRef, canSettle, today, orders, action, keyFor, clearKey, setMessage, askReason,
  });
  const { payments, paymentDetailsId, setPaymentDetailsId } = pay;

  useEffect(() => {
    if (busy) return;
    const rowId = focusAfterWrite.current;
    if (rowId === null) return;
    focusAfterWrite.current = null;
    // The workspace is what the user asked for; the row is where they were
    // when the request failed and left them nothing else.
    if (orderPanelHeadingRef.current !== null) orderPanelHeadingRef.current.focus();
    else focusPhoneRow(rowId);
  }, [busy]);

  // `run` (from useDialogAction) also wraps two READS — "open:<id>" and "more"
  // — which keep the flight guard but deliberately get no BusyButton treatment
  // (#236 is writes). Pending scopes are independent of the idempotency key
  // scopes built inside each action (nothing couples the two).
  //
  // Closing a dialog is the screen's state; the two bookkeeping calls that end
  // its session and mute its attempt are the hook's (`dismissDialog`). Cancel
  // stays live during `busy`, and Escape and the backdrop dismiss too (#474).
  const closeOrderPanel = () => {
    dismissDialog("order-panel");
    setActive(null);
  };

  const { onCreateOrder, onAddItem, onUpdateItem, onRemoveItem, onConfirm, onCancel, onVoid, onOpen } = useOrderCommands({
    action, orders, keyFor, clearKey, setMessage, dialogs: { confirm, askReason, askChoice },
    activeOrder, newOrder, addLine, products, productName, eggsPerUnit, setConversions, setAllProducts, setProducts,
  });

  // A list failure no longer replaces the workspace: it renders as a banner
  // beside it (below), so a transient blip cannot discard an order the user
  // is part-way through editing (#469). Only the setup reads — customers and
  // products, without which no form on this screen can function — still gate
  // the page.
  if (setupError) return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="error">{setupError}</p></FieldConsole>;
  if (orders.rows === null) return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="muted">{t("loading")}</p></FieldConsole>;

  // Adjusted during render, the React-documented way to reset state a prop (or
  // here, a refreshed list) invalidates: an id whose record left the window is
  // retired, so a later load cannot silently reopen the peek on it.
  if (detailsId !== null && !orders.rows.some((o) => o.id === detailsId)) setDetailsId(null);
  if (paymentDetailsId !== null && payments !== null
    && !payments.items.some((p) => p.id === paymentDetailsId)) setPaymentDetailsId(null);
  const details = orders.rows.find((o) => o.id === detailsId) ?? null;
  const paymentDetails = payments?.items.find((p) => p.id === paymentDetailsId) ?? null;

  const outstanding = active?.status === "Confirmed" && payments
    ? payments.outstandingMinorUnits : active?.outstandingMinorUnits;

  const filters = <OrderFilters filter={listFilter} canSettle={canSettle} isDesktop={isDesktop} />;

  // #655 — withheld exactly when the truly-empty state below is offering this
  // same action (never for the filtered-empty branch, which offers "Clear
  // filters" instead — no duplicate there).
  const newOrderButton = customers.length > 0 && !(orders.rows.length === 0 && !hasActiveFilter) && (
    <Button variant="contained" type="button" sx={{ minHeight: 44, borderRadius: "4px", flexShrink: 0 }} onClick={openNewOrder}>
      <Plus size={16} aria-hidden /> {t("newOrder")}
    </Button>
  );
  const intro = <Typography variant="body2" className="muted" sx={{ fontSize: ".8125rem" }}>{t("intro")}</Typography>;

  return (
    <FieldConsole>
      {isDesktop ? (
        <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: 1.5, alignItems: "start" }}>
          <Box><Typography variant="h2">{t("title")}</Typography>{intro}</Box>
          {newOrderButton}
        </Box>
      ) : (
        // "Sales" is short enough to share its row with the create action, which
        // is worth ~52px of list on a phone (#987).
        <>
          <Stack direction="row" sx={{ alignItems: "flex-start", justifyContent: "space-between", gap: 1, mb: 1 }}>
            <Typography variant="h2">{t("title")}</Typography>
            {newOrderButton}
          </Stack>
          {intro}
        </>
      )}

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

      {/* #727 — persistent, like the notice above, and for the same reason:
          the limit has to be known while a price is being typed, not met as a
          refusal after one has been. Shown whenever a ceiling binds this
          caller, breach or no breach. */}
      {ceiling !== null && (
        <p className="hint" role="status" data-testid="discount-ceiling-notice">
          {t("discountCeilingNotice", { percent: ceilingPercent })}
        </p>
      )}

      {/* Native form validation would intercept the page's own money validation messages. */}
      <NewOrderDialog newOrder={newOrder} action={action} today={today} onCreateOrder={onCreateOrder} />

      {active && <ConsoleSummary label={t("orderContext")} items={[
        { label: t("reference"), value: `${active.referenceNumber} · ${statusLabel(active.status)}` },
        { label: t("total"), value: fmt.money(active.totalMinorUnits, active.currencyCode, active.currencyMinorUnit) },
        ...(canSettle ? [{ label: t("outstanding"), value: outstanding == null ? "—" : fmt.money(outstanding, active.currencyCode, active.currencyMinorUnit) }] : []),
      ]} />}

      {active && (
        <Box sx={{ my: 3 }} role="region" aria-labelledby={orderPanelHeadingId}>
          <Box sx={{ ...CONSOLE_SPLIT_SX, gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, 1.6fr) minmax(240px, .65fr)" } }}>
            <Box sx={CONSOLE_PANEL_SX}>
              <Box component="header" sx={{ display: "flex", alignItems: "start", justifyContent: "space-between", gap: "15px", pb: 1.75, mb: 1.75, borderBottom: "1px solid var(--rule)" }}>
                <Box>
                  <Typography variant="overline" sx={{ fontSize: ".625rem", letterSpacing: ".12em", textTransform: "uppercase", fontWeight: 750, color: "text.secondary" }}>{active.status === "Draft" ? t("draftOrderHeading") : t("orderHeading")}</Typography>
                  <Typography variant="h3" component="h3" id={orderPanelHeadingId} ref={orderPanelHeadingRef} tabIndex={-1} sx={{ "&&": { m: 0 } }}>
                    {active.referenceNumber} — {rowCustomerName(active)}
                  </Typography>
                </Box>
                <OrderStatus status={active.status} />
              </Box>
              {active.items.length > 0 && (
                <OrderLines active={active} activeOrder={activeOrder} ceiling={ceiling} action={action} editQtyId={editQtyId}
                  productName={productName} onUpdateItem={onUpdateItem} onRemoveItem={onRemoveItem} />
              )}
              {active.status === "Draft" && (
                <AddLineForm active={active} fields={addLine} products={products} priceScale={priceScale}
                  eggsPerUnit={eggsPerUnit} unitWord={unitWord} addQtyId={addQtyId} action={action} onAddItem={onAddItem} />
              )}
            </Box>
            <SettlementRail active={active} activeOrder={activeOrder} ceiling={ceiling} ceilingPercent={ceilingPercent}
              canSettle={canSettle} isAdmin={isAdmin} action={action} onCancel={onCancel} onConfirm={onConfirm}
              onVoid={onVoid} closeOrderPanel={closeOrderPanel}
              paymentsPanel={payments && (
                <PaymentsPanel pay={pay} payments={payments} paymentDetails={paymentDetails} isDesktop={isDesktop}
                  isAdmin={isAdmin} action={action} today={today} />
              )} />
          </Box>
        </Box>
      )}

      {/* The page's own copy, for everything not behind a dialog — and for a
          failure that is nobody's dialog even while one is open, rather than
          swallowing it. Unconditional: a dialog's message lives in a slot only
          that dialog reads, so there is nothing here to double up on (#474). */}
      {errors.page && <p className="error">{errors.page}</p>}
      {message && <p className="success">{message}</p>}

      {!isDesktop && <>
        {/* One 36px chip row in place of four stacked filter controls; each
            chip opens the same dialog. `keepMounted` so the customer picker
            still resolves a deep-linked `customerId` while it is closed. */}
        <Stack direction="row" sx={{ gap: 1, mb: 1.5, flexWrap: "wrap" }}>
          <Button variant="outlined" color="inherit" sx={CHIP_SX} onClick={() => setFilterOpen(true)}>
            {statusFilter ? statusLabel(statusFilter) : t("allStatusesChip")}
          </Button>
          <Button variant="outlined" color="inherit" sx={CHIP_SX} onClick={() => setFilterOpen(true)}>
            {customerFilterName ?? t("allCustomersChip")}
          </Button>
          {canSettle && <Button variant={unpaidFilter ? "contained" : "outlined"} color={unpaidFilter ? "primary" : "inherit"}
            sx={CHIP_SX} onClick={() => setFilterOpen(true)}>{t("unpaidOnlyFilter")}</Button>}
        </Stack>
        <Dialog open={filterOpen} keepMounted title={t("filtersTitle")} onClose={() => setFilterOpen(false)}
          actions={<Stack direction="row" sx={{ justifyContent: "flex-end", alignItems: "center", gap: 1 }}>
            <Button size="small" sx={CONSOLE_LINK_SX} onClick={clearFilters}>{tc("clearFiltersButton")}</Button>
            <Button variant="contained" sx={{ minHeight: 44, borderRadius: "4px" }} onClick={() => setFilterOpen(false)}>{t("filtersDoneButton")}</Button>
          </Stack>}>
          {filters}
        </Dialog>
        {/* A read-only peek onto the row, carrying no action the row did not
            already have. Every action closes it first, so a failure lands on
            the page where it is visible rather than behind this dialog. */}
        <OrderDetailsDialog details={details} canSettle={canSettle} isAdmin={isAdmin} busy={busy}
          focusAfterWrite={focusAfterWrite} rowCustomerName={rowCustomerName} onOpen={onOpen} setDetailsId={setDetailsId} />
      </>}
      {isDesktop ? <h3>{t("ordersHeading")}</h3>
        : <ConsoleSubhead title={t("ordersHeading")} caption={t("ordersCaption")} />}
      {isDesktop && filters}
      {/* The list's own failure, beside the workspace rather than instead of
          it — and self-healing on the next successful load (#469). */}
      {orders.error && <p className="error" role="alert">{orders.error}</p>}
      {/* One window's orders must never sit under another window's filters,
          not even for the length of the request (#469). FR-050: `customerFilterStale`
          extends the hide to cover the render(s) between a URL identity change
          and `reloading` taking over — see its declaration in useOrderFilters.ts. */}
      {(orders.reloading || customerFilterStale) ? (
        <p className="muted">{t("loading")}</p>
      ) : orders.rows.length === 0 ? (
        hasActiveFilter
          ? <EmptyState icon={FilterX} message={t("noOrdersMatch")} />
          // Same condition AND same handler as the page-head New order button
          // above — a customer-less farm gets the sentence alone there too.
          : <EmptyState icon={ShoppingCart} message={t("noOrdersMessage")}
              action={customers.length > 0 ? { label: t("newOrder"), onClick: openNewOrder } : undefined} />
      ) : (
        <OrderList orders={orders} rows={orders.rows} isDesktop={isDesktop} canSettle={canSettle} isAdmin={isAdmin}
          busy={busy} fieldId={fieldId} rowCustomerName={rowCustomerName} onOpen={onOpen} setDetailsId={setDetailsId} />
      )}

      {confirmDialog}
    </FieldConsole>
  );
}
