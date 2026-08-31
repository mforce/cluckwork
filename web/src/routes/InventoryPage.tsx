import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import {
  createInventoryItem, activateInventoryItem, deactivateInventoryItem, formatMoney, getAccount,
  listInventoryItems, listInventoryLots, listInventoryMovements, parseMoneyToMinorUnits,
  recordInventoryAdjustment, recordInventoryPurchase, updateInventoryItem,
} from "../api/cluckwork";
import type { Account, InventoryItem, InventoryLot, InventoryMovement } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { StatusBadge } from "../components/StatusBadge";
import { usePagedList } from "../components/usePagedList";
import { useDialogErrors } from "../components/useDialogErrors";
import { usePendingAction } from "../components/usePendingAction";
import { newId } from "../lib/ids";
import { useFarmToday } from "../farm/useFarm";
import { FEEDABLE_CATEGORIES } from "./FeedPage";
import i18n from "../i18n";
import { inventoryCategoryLabel, inventoryMovementLabel, statusLabel } from "../i18n/enums";

// Feed first (spec §12); the rest of the categories get their features later.
const CATEGORIES = [
  "Feed", "Supplement", "Additive", "Medication", "Vaccine",
  "Packaging", "Bedding", "Sanitation", "EquipmentPart", "Other",
];



function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// The ledger's previous hard cap, kept as the page size.
const LEDGER_PAGE = 100;

// F15 (#66, PR 1): inventory catalog + receiving stock. Items define what and
// how it's measured; lots carry quantities/cost; the movement ledger explains
// every change. Feed usage (consumption) is the follow-up PR.
export function InventoryPage() {
  const { t } = useTranslation("inventory");
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  // Purchases and feed usage are the day's work — open to everyone. The item
  // catalog and stock corrections are admin-only (#73).
  const { isAdmin } = useAuth();
  const [items, setItems] = useState<InventoryItem[] | null>(null);
  // Account currency drives ALL money parsing/formatting here — costs may not
  // exist on an item yet, and assuming 2 decimals corrupts JPY/KWD amounts.
  const [account, setAccount] = useState<Account | null>(null);
  // #479 — one slot per PLACE a message can appear: the page, and each of the
  // four dialogs below by its own scope.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;
  const [message, setMessage] = useState<string | null>(null);
  // #236: the flight guard + per-scope spinner state live in the shared hook;
  // this screen keeps only its idempotency-key and refresh discipline below.
  const { busy, isPending, run: runPending } = usePendingAction();

  // create form (F131: every capture form on this screen is a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [category, setCategory] = useState("Feed");
  const [unit, setUnit] = useState("kg");
  const [defaultCost, setDefaultCost] = useState("");

  // edit — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editUnit, setEditUnit] = useState("");
  const [editCost, setEditCost] = useState("");

  // open item panel: purchase/adjust forms + ledger. Feed usage moved to its
  // own /feed page (#446) — the panel keeps only a deep link there.
  const [active, setActive] = useState<InventoryItem | null>(null);
  const [lots, setLots] = useState<InventoryLot[]>([]);
  // the open item's two capture dialogs
  const [purchasing, setPurchasing] = useState(false);
  const [adjusting, setAdjusting] = useState(false);
  // adjustment form
  const [adjustLotId, setAdjustLotId] = useState("");
  const [adjustType, setAdjustType] = useState("Adjustment");
  const [adjustQty, setAdjustQty] = useState("");
  const [adjustReason, setAdjustReason] = useState("");
  const [purchaseDate, setPurchaseDate] = useState(today);
  const [purchaseQty, setPurchaseQty] = useState("");
  const [purchaseCost, setPurchaseCost] = useState("");
  const [lotNumber, setLotNumber] = useState("");
  const [expiryDate, setExpiryDate] = useState("");
  const [purchaseNote, setPurchaseNote] = useState("");

  // #511 — the movement ledger is server-paged; the OPEN ITEM is the fetch
  // identity, so switching items reloads from the top and a late response for
  // the previous item can never paint under this one's heading.
  const activeId = active?.id ?? null;
  const fetchMovements = useCallback(
    (offset: number, limit: number) =>
      activeId
        ? listInventoryMovements(activeId, { limit, offset })
        : Promise.resolve<InventoryMovement[]>([]),
    [activeId],
  );
  const ledger = usePagedList<InventoryMovement>({
    fetchPage: fetchMovements,
    pageSize: LEDGER_PAGE,
    // The LEDGER's own key, not the item list's: four existing ledger-failure
    // tests assert this exact sentence, and `onOpen`'s catch used it too.
    errorText: () => i18n.t("inventory:loadLedgerFailed"),
  });

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const fetchItems = () => listInventoryItems({ includeInactive: true });

  useEffect(() => {
    Promise.all([fetchItems(), getAccount()])
      .then(([list, acct]) => {
        setItems(list);
        setAccount(acct);
      })
      .catch(() => setPageError(i18n.t("inventory:loadInventoryFailed")));
  }, []);

  // Dismissal empties a dialog's slot and mutes the attempt still out, so a
  // late failure is not reported against a session the user reopened.
  const closeCreate = () => { setCreating(false); errors.abandon("create"); };
  const closeEdit = () => {
    const id = editingId;
    setEditingId(null);
    if (id) errors.abandon(`edit:${id}`);
  };
  const closePurchase = () => { setPurchasing(false); errors.abandon("purchase"); };
  const closeAdjust = () => { setAdjusting(false); errors.abandon("adjust"); };

  async function refreshAll(openItemId?: string) {
    const fresh = await fetchItems();
    setItems(fresh);
    const target = openItemId ?? active?.id;
    if (target) {
      const stillThere = fresh.find((i) => i.id === target) ?? null;
      setActive(stillThere);
      if (stillThere) await loadLots(stillThere.id);
    }
  }

  // Lots are deliberately NOT folded into the paged ledger's `meta`: the hook
  // sets meta from every page it fetches, so a window refresh would re-request
  // the lots once per page. They keep their own read and their own guard.
  const lotsRequest = useRef(0);

  // #511 round 4 — an `activeIdRef.current !== itemId` guard was added here in
  // round 2 and removed again in round 3, deliberately. Why it was redundant,
  // stated as the argument that actually holds:
  //   * Both callers set `active` to the same item SYNCHRONOUSLY before
  //     calling this — `onOpen` does setActive(i) then loadLots(i.id);
  //     `refreshAll` does setActive(stillThere) then loadLots(stillThere.id) —
  //     so the ids agree at call time and a call-time comparison is a no-op.
  //   * A divergence can only appear AFTER the await, and that is exactly what
  //     the `lotsRequest` ticket below rejects: opening any item bumps it.
  //   * `usePendingAction`'s `busy` disables every row's open button for the
  //     whole of a write, so the interleaving the ref form would have caught
  //     cannot be produced from the UI.
  // The round-3 comment justified this by saying the suite stayed green when
  // the guard was deleted. That was the wrong evidence and is not repeated
  // here: a green suite after a deletion proves no test REACHES the branch,
  // which is a coverage fact, not an unreachability proof.
  // The ticket is now the sole guard on this read, and it has no test of its
  // own — measured, not assumed: disabling it leaves the whole suite green.
  // That gap is tracked as #631.
  async function loadLots(itemId: string) {
    const req = ++lotsRequest.current;
    let lotRows: InventoryLot[];
    try {
      lotRows = await listInventoryLots(itemId);
    } catch (err) {
      // #511 round 6 — INV-1 applies to the failure path, but INV-6 still owns
      // the live one, and the two are distinguishable by the same ticket that
      // already guards the success path:
      //   * SUPERSEDED (the ticket moved on): nobody is waiting for this and
      //     the panel is showing a different item. Swallow it — neither report
      //     nor throw. Painting it over a healthy view is the bug the hook's
      //     own header calls "a superseded request's rejection is exactly as
      //     stale as its response".
      //   * CURRENT: rethrow, unchanged. `refreshAll` awaits this with no catch
      //     of its own, so the throw reaches `run()`, which reports it into the
      //     write's dialog and SKIPS clearKey — that is how a write whose
      //     post-write refresh failed keeps its idempotency key for a replay.
      // An earlier draft of this fix swallowed BOTH and would have reported
      // such a write as successful. It is pinned now by "fails the write and
      // keeps its key when the post-write LOTS re-read fails".
      if (lotsRequest.current !== req) return;
      throw err;
    }
    if (lotsRequest.current !== req) return;
    setLots(lotRows);
    setAdjustLotId((prev) => lotRows.some((l) => l.id === prev) ? prev : (lotRows[0]?.id ?? ""));
  }

  // `scope` drives the idempotency key + pending spinner (#236); `errorScope`
  // is the #479 slot the attempt reports to — `null` for the two row-level
  // writes (activate/deactivate) that have no dialog of their own.
  // #511 round 2 — `touchesLedger` decides whether this write goes through the
  // LEDGER's runWrite. Only a write that produces an InventoryMovement does.
  // Round 1 wrapped all six, so an unrelated create-item claimed the open
  // ledger's ticket: it disabled that ledger's Load more for the duration
  // (canLoadMore folds in `loading`), re-walked every loaded page for nothing,
  // and could clear a standing ledger error the moment its incidental re-read
  // happened to succeed. FlocksPage already draws this line — only
  // onRecordMovement goes through ledger.runWrite there.
  async function run(scope: string, errorScope: string | null, action: (key: string) => Promise<unknown>, openItemId?: string, touchesLedger = false): Promise<boolean> {
    const outcome = await runPending(scope, async () => {
      errors.beginAttempt(errorScope);
      setMessage(null);
      try {
        const write = async () => {
          await action(keyFor(scope));
          // The refresh must succeed before the key rotates: if it throws, the key
          // survives and a retry replays the idempotent write instead of repeating it.
          await refreshAll(openItemId);
        };
        // A ledger write refreshes the whole loaded movement window (AC4); a
        // catalog write refreshes items and lots only.
        if (touchesLedger) await ledger.runWrite(write);
        else await write();
        clearKey(scope);
        return true;
      } catch (err) {
        errors.report(errorScope, errText(err));
        return false;
      }
    });
    // A skipped run (another flight already open) reports `undefined` — never
    // success: mapping it to false keeps a blocked submit from closing its
    // dialog or resetting its form as if it had saved.
    return outcome ?? false;
  }

  const minorUnit = account?.currencyMinorUnit ?? 2;
  const costStep = 10 ** -minorUnit;

  function toMinorUnits(text: string): number | null {
    if (!text.trim()) return null;
    const parsed = parseMoneyToMinorUnits(text, minorUnit);
    if (!Number.isFinite(parsed) || parsed < 0) throw new Error(i18n.t("inventory:invalidCostError"));
    return parsed;
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-item", "create", (key) =>
      createInventoryItem({
        name, category, unit,
        defaultUnitCostMinorUnits: toMinorUnits(defaultCost),
      }, key));
    if (ok) {
      setName("");
      setDefaultCost("");
      setMessage(i18n.t("inventory:itemCreatedMessage"));
      setCreating(false);
    }
  }

  function startEdit(i: InventoryItem) {
    closeCreate();
    // A different item's edit DISPLACES this one: the session ends without
    // onClose, and its per-id slot would otherwise replay the dead session's
    // failure when THAT item's edit is reopened later. Reachable behind the
    // backdrop via a screen reader's virtual cursor (#480; pi review of #491).
    if (editingId !== null && editingId !== i.id) errors.abandon(`edit:${editingId}`);
    setEditingId(i.id);
    setEditName(i.name);
    setEditUnit(i.unit);
    setEditCost(i.defaultCostMinorUnits === null
      ? ""
      : (i.defaultCostMinorUnits / 10 ** minorUnit).toFixed(minorUnit));
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    const ok = await run(`update:${id}`, `edit:${id}`, (key) =>
      updateInventoryItem(id, {
        name: editName, unit: editUnit,
        defaultUnitCostMinorUnits: toMinorUnits(editCost),
      }, key));
    if (ok) setEditingId(null);
  }

  async function onOpen(i: InventoryItem) {
    // The purchase/adjust dialogs are bound to the ACTIVE panel — an open one
    // would otherwise REBIND in place when the item switches: its title
    // changes but the typed quantity/cost and any error do not, so it would
    // spring back open over the new item carrying the old item's form and
    // verdict. Closing it (not just abandoning the error scope) is what the
    // Close button below should have done too — this covers a panel closed
    // via #480's virtual-cursor door and then a DIFFERENT item opened, which
    // otherwise skips the guard entirely (`active` is null by then).
    // Checking `active === null` as well as an id mismatch covers a panel
    // closed via #480's virtual-cursor door and then a DIFFERENT item
    // opened, which otherwise skipped this entirely (nothing reset
    // `purchasing`/`adjusting` on close, and `active` reads null by then,
    // so an id comparison alone missed it). Re-opening the SAME still-active
    // item is spared, same as every other displacement guard in this file.
    if (active === null || active.id !== i.id) {
      closePurchase();
      closeAdjust();
      // A different item's lots must never be visible under this one, not even
      // for the length of the fetch.
      setLots([]);
      setAdjustLotId("");
    }
    // #511 round 2 — the hook reloads only when `activeId` CHANGES, so
    // re-opening the item that is already open would leave the movement
    // ledger stale while the lots beside it refreshed. The pre-#511 code
    // re-read the ledger on every Open click; `reload()` restores that.
    const sameItem = active !== null && active.id === i.id;
    setActive(i);
    if (sameItem) void ledger.reload();
    try {
      await loadLots(i.id);
    } catch {
      // Names the read that actually failed. Before #511 split the combined
      // movements+lots read, this catch covered both and the ledger wording
      // was accurate; it only wraps loadLots now.
      setPageError(i18n.t("inventory:loadLotsFailed"));
    }
  }

  async function onPurchase(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    // Clears the purchase slot whether or not the check below fails, so a
    // fixed keystroke doesn't leave a stale verdict behind.
    errors.beginAttempt("purchase");
    const qty = parseFloat(purchaseQty);
    if (!Number.isFinite(qty) || qty <= 0) {
      errors.report("purchase", i18n.t("inventory:quantityMustBePositive"));
      return;
    }
    const ok = await run(`purchase:${active.id}`, "purchase", (key) =>
      recordInventoryPurchase(active.id, {
        receivedDate: purchaseDate,
        quantity: qty,
        unitCostMinorUnits: toMinorUnits(purchaseCost),
        lotNumber: lotNumber.trim() || undefined,
        expiryDate: expiryDate || undefined,
        note: purchaseNote.trim() || undefined,
      }, key), active.id, true);
    if (ok) {
      setPurchaseQty("");
      setPurchaseCost("");
      setLotNumber("");
      setExpiryDate("");
      setPurchaseNote("");
      setMessage(i18n.t("inventory:purchaseRecordedMessage"));
      setPurchasing(false);
    }
  }

  async function onAdjust(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    // Same reasoning as onPurchase: cleared up front so either guard below
    // reports against a clean slot.
    errors.beginAttempt("adjust");
    const delta = parseFloat(adjustQty);
    if (!Number.isFinite(delta) || delta === 0) {
      errors.report("adjust", i18n.t("inventory:adjustQuantityRequired"));
      return;
    }
    if (!adjustReason.trim()) {
      errors.report("adjust", i18n.t("inventory:adjustReasonRequired"));
      return;
    }
    const ok = await run(`adjust:${active.id}:${adjustLotId}`, "adjust", (key) =>
      recordInventoryAdjustment(active.id, {
        inventoryLotId: adjustLotId,
        date: today,
        type: adjustType,
        quantityDelta: adjustType === "Discard" ? -Math.abs(delta) : delta,
        reason: adjustReason.trim(),
      }, key), active.id, true);
    if (ok) {
      setAdjustQty("");
      setAdjustReason("");
      setMessage(i18n.t("inventory:correctionRecordedMessage"));
      setAdjusting(false);
    }
  }

  const lotLabel = (l: InventoryLot) =>
    `${l.receivedDate}${l.lotNumber ? ` · ${l.lotNumber}` : ""} — ${l.quantityAvailable}/${l.quantityReceived}`;

  const costText = (i: InventoryItem) =>
    i.defaultCostMinorUnits !== null && i.defaultCostCurrencyCode
      ? formatMoney(i.defaultCostMinorUnits, i.defaultCostCurrencyCode,
          i.defaultCostCurrencyMinorUnit ?? minorUnit)
      : "—";

  if (errors.page && items === null) {
    return <section><h2>{t("title")}</h2><p className="error">{errors.page}</p></section>;
  }
  if (items === null) {
    return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  }

  const canFeed = active !== null && FEEDABLE_CATEGORIES.includes(active.category);

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {isAdmin && (
          <button type="button" onClick={() => { closeEdit(); setCreating(true); }}>
            <Plus size={16} aria-hidden /> {t("newItemButton")}
          </button>
        )}
      </div>
      <p className="muted">
        {t("intro")}
      </p>

      {/* Gated like the inline form was: a role change mid-edit closes it. */}
      <Dialog open={creating && isAdmin} title={t("newItemDialogTitle")} onClose={closeCreate}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>{t("itemNameLabel")}
            <input value={name} required maxLength={200}
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>{t("categoryLabel")}
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              {CATEGORIES.map((c) => <option key={c} value={c}>{inventoryCategoryLabel(c)}</option>)}
            </select>
          </label>
          <label>{t("unitLabel")}
            <input value={unit} required maxLength={20}
              onChange={(e) => setUnit(e.target.value)} />
          </label>
          <label>{t("defaultCostLabel")}
            <input className="cell" type="number" min={0} step={costStep} value={defaultCost}
              onChange={(e) => setDefaultCost(e.target.value)} />
          </label>
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("create-item")} disabled={busy}>{t("addItemButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      <Dialog open={editingId !== null && isAdmin} title={t("editItemDialogTitle")} onClose={closeEdit}>
        {/* noValidate: the row's save used to be a plain button, so the browser
            never enforced min/step — toMinorUnits' own message did. */}
        <form className="inline-form" noValidate onSubmit={onSaveEdit}>
          <label>{t("editItemNameLabel")}
            <input value={editName} maxLength={200}
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <label>{t("editUnitLabel")}
            <input value={editUnit} maxLength={20}
              onChange={(e) => setEditUnit(e.target.value)} />
          </label>
          <label>{t("defaultCostLabel")}
            <input className="cell" type="number" min={0} step={costStep} value={editCost}
              onChange={(e) => setEditCost(e.target.value)} />
          </label>
          <DialogError errors={errors} scope={`edit:${editingId}`} />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={editingId !== null && isPending(`update:${editingId}`)} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </div>
        </form>
      </Dialog>

      {/* Unconditional since #479 — a dialog's failure lives in its own slot
          now, so the page copy can't inherit it. */}
      {errors.page && <p className="error">{errors.page}</p>}
      {message && <p className="success">{message}</p>}

      {active && (
        <div className="order-panel">
          <h3>{t("itemPanelHeading", { name: active.name, quantity: active.quantityOnHand, unit: active.unit })}</h3>

          {/* One row of actions; each opens its own dialog so the ledger below
              stays put instead of being pushed down by three stacked forms. */}
          <div className="panel-actions">
            <button type="button" onClick={() => setPurchasing(true)}>
              <Plus size={16} aria-hidden /> {t("recordPurchaseButton")}
            </button>
            {canFeed && (
              // #446 — feed usage lives on its own page now; the deep link
              // keeps the one thing the old dialog had over it: the item you
              // are looking at arrives preselected.
              <Link className="link" to={`/feed?item=${active.id}`}>
                {t("recordUsageLink")}
              </Link>
            )}
            {isAdmin && lots.length > 0 && (
              <button type="button" className="link" onClick={() => setAdjusting(true)}>
                {t("correctStockButton")}
              </button>
            )}
          </div>

          {/* Why an action is unavailable, in the place the button would be. */}
          {!canFeed && (
            <p className="muted">
              {t("notFeedableMessage", { category: inventoryCategoryLabel(active.category) })}
            </p>
          )}
          {!isAdmin ? (
            <p className="muted">{t("correctionsNeedAdminMessage")}</p>
          ) : lots.length === 0 ? (
            <p className="muted">{t("noLotsMessage")}</p>
          ) : null}

          <Dialog open={purchasing} title={t("recordPurchaseDialogTitle", { name: active.name })} onClose={closePurchase}>
            <form className="form-grid" onSubmit={onPurchase}>
              <label>{t("receivedLabel")}
                <input type="date" value={purchaseDate} max={today} required
                  onChange={(e) => setPurchaseDate(e.target.value)} />
              </label>
              <label>{t("quantityLabelWithUnit", { unit: active.unit })}
                <input type="number" min={0.001} step={0.001} value={purchaseQty} required
                  onChange={(e) => setPurchaseQty(e.target.value)} />
              </label>
              <label>{active.defaultCostCurrencyCode
                ? t("unitCostWithCurrencyLabel", { code: active.defaultCostCurrencyCode })
                : t("unitCostLabel")}
                <input type="number" min={0} step={costStep} value={purchaseCost}
                  placeholder={active.defaultCostMinorUnits !== null ? t("costPlaceholderItemDefault") : t("costPlaceholderRequired")}
                  onChange={(e) => setPurchaseCost(e.target.value)} />
              </label>
              <label>{t("lotNumberLabel")}
                <input value={lotNumber} maxLength={100}
                  onChange={(e) => setLotNumber(e.target.value)} />
              </label>
              <label>{t("expiryLabel")}
                <input type="date" value={expiryDate} min={purchaseDate}
                  onChange={(e) => setExpiryDate(e.target.value)} />
              </label>
              <label>{t("noteLabel")}
                <input value={purchaseNote} maxLength={500}
                  onChange={(e) => setPurchaseNote(e.target.value)} />
              </label>
              <DialogError errors={errors} scope="purchase" />
              <div className="dialog-foot">
                <button type="button" className="link" onClick={closePurchase}>{tc("cancel")}</button>
                <BusyButton type="submit" busy={isPending(`purchase:${active.id}`)} disabled={busy}>
                  {t("recordPurchaseSubmitButton")}
                </BusyButton>
              </div>
            </form>
          </Dialog>

          <Dialog open={adjusting && isAdmin} title={t("correctStockDialogTitle", { name: active.name })} onClose={closeAdjust}>
            <form className="form-grid" onSubmit={onAdjust}>
              {/* Disabled during any flight: the composite adjust scope embeds
                  the selected lot id, so changing the selection mid-flight
                  would re-point isPending at a scope nobody is running and
                  drop the spinner while the request is still open (#242). */}
              <label>{t("lotFieldLabel")}
                <select value={adjustLotId} disabled={busy}
                  onChange={(e) => setAdjustLotId(e.target.value)}>
                  {lots.map((l) => <option key={l.id} value={l.id}>{lotLabel(l)}</option>)}
                </select>
              </label>
              <label>{t("typeLabel")}
                <select value={adjustType} onChange={(e) => setAdjustType(e.target.value)}>
                  <option value="Adjustment">{t("adjustTypeAdjustmentOption")}</option>
                  <option value="Discard">{t("adjustTypeDiscardOption")}</option>
                </select>
              </label>
              <label>{t("quantityLabelWithUnit", { unit: active.unit })}
                <input type="number" step={0.001} value={adjustQty} required
                  placeholder={adjustType === "Discard" ? t("adjustQuantityPlaceholderDiscard") : t("adjustQuantityPlaceholderCorrection")}
                  onChange={(e) => setAdjustQty(e.target.value)} />
              </label>
              <label>{t("reasonLabel")}
                <input value={adjustReason} maxLength={500} required
                  onChange={(e) => setAdjustReason(e.target.value)} />
              </label>
              <DialogError errors={errors} scope="adjust" />
              <div className="dialog-foot">
                <button type="button" className="link" onClick={closeAdjust}>{tc("cancel")}</button>
                {/* The composite key scope doubles as the pending scope (#236). */}
                <BusyButton type="submit" busy={isPending(`adjust:${active.id}:${adjustLotId}`)}
                  disabled={busy || !adjustLotId}>
                  {t("recordCorrectionButton")}
                </BusyButton>
              </div>
            </form>
          </Dialog>

          {/* #511 round 5 — the error renders BESIDE the rows, never instead of
              them. usePagedList keeps `rows` and `hasMore` when an EXTENSION
              fails (only a failed REPLACEMENT empties them), so a branch that
              swapped the table for the message threw away everything the user
              had paged to over one transient load-more failure. That is AC3:
              a failed extension keeps already-loaded rows and permits retry.
              CustomersPage had this right from the start — it is the shape
              copied here. A failed REPLACEMENT still shows the message alone,
              because the hook has emptied `rows` by then and the empty branch
              below does not fire on `error`. */}
          {ledger.error && <p className="error">{ledger.error}</p>}
          {ledger.rows === null || ledger.reloading ? (
            <p className="muted">{tc("loading")}</p>
          ) : ledger.rows.length === 0 && !ledger.error ? (
            <p className="muted">{t("noMovementsMessage")}</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>{t("ledgerDateHeader")}</th><th>{t("ledgerTypeHeader")}</th><th>{t("ledgerQuantityHeader")}</th><th>{t("ledgerNoteHeader")}</th></tr>
              </thead>
              <tbody>
                {ledger.rows.map((m) => (
                  <tr key={m.id}>
                    <td>{m.date}</td>
                    <td>{inventoryMovementLabel(m.type)}</td>
                    <td>{m.quantityDelta > 0 ? `+${m.quantityDelta}` : m.quantityDelta} {m.unit}</td>
                    <td>{m.note ?? ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {ledger.canLoadMore && (
            <button className="link" onClick={() => void ledger.loadMore()}>
              {t("loadMoreButton")}
            </button>
          )}
          <div className="actions">
            <button className="link" onClick={() => setActive(null)}>{t("closeButton")}</button>
          </div>
        </div>
      )}

      <table className="data">
        <thead>
          <tr><th>{t("nameHeader")}</th><th>{t("categoryHeader")}</th><th>{t("onHandHeader")}</th><th>{t("defaultCostHeader")}</th><th>{t("statusHeader")}</th><th></th></tr>
        </thead>
        <tbody>
          {items.map((i) => (
            <tr key={i.id} className={i.active ? undefined : "inactive"}>
              <td>{i.name}</td>
              <td>{inventoryCategoryLabel(i.category)}</td>
              <td>{i.quantityOnHand} {i.unit}</td>
              <td>{costText(i)}</td>
              <td><StatusBadge status={i.active ? "Active" : "Inactive"} label={statusLabel(i.active ? "Active" : "Inactive")} /></td>
              <td>
                <button className="link" disabled={busy} onClick={() => void onOpen(i)}>{t("openButton")}</button>
                {isAdmin && (
                  <>
                    {/* Opens the edit dialog — non-mutating, so the spinner
                        belongs to the dialog's Save, not here (#242). */}
                    <button className="link" disabled={busy}
                      onClick={() => startEdit(i)}>{t("editButton")}</button>
                    {i.active ? (
                      <BusyButton className="link" busy={isPending(`deactivate:${i.id}`)} disabled={busy}
                        onClick={() => void run(`deactivate:${i.id}`, null, (key) => deactivateInventoryItem(i.id, key))}>
                        {t("deactivateButton")}
                      </BusyButton>
                    ) : (
                      <BusyButton className="link" busy={isPending(`activate:${i.id}`)} disabled={busy}
                        onClick={() => void run(`activate:${i.id}`, null, (key) => activateInventoryItem(i.id, key))}>
                        {t("activateButton")}
                      </BusyButton>
                    )}
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
