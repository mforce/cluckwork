import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import {
  createInventoryItem, activateInventoryItem, deactivateInventoryItem, getAccount,
  listInventoryItems, listInventoryLots, listInventoryMovements, parseMoneyToMinorUnits,
  recordInventoryAdjustment, recordInventoryPurchase, updateInventoryItem,
} from "../api/cluckwork";
import type { Account, InventoryItem, InventoryLot, InventoryMovement } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { StatusBadge } from "../components/StatusBadge";
import { usePagedList } from "../components/usePagedList";
import { useDialogAction } from "../components/useDialogAction";
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



// The ledger's previous hard cap, kept as the page size.
const LEDGER_PAGE = 100;

// The scopes that own a dialog (#703). `run` routes a failure by this and gates
// a success by it; a scope outside the list — activate/deactivate from the row
// buttons — reports to the page and is never superseded.
const DIALOG_SCOPES = ["create", "edit", "purchase", "adjust"] as const;

// F15 (#66, PR 1): inventory catalog + receiving stock. Items define what and
// how it's measured; lots carry quantities/cost; the movement ledger explains
// every change. Feed usage (consumption) is the follow-up PR.
export function InventoryPage() {
  const { t } = useTranslation("inventory");
  const fmt = useFormat();
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
  const [message, setMessage] = useState<string | null>(null);
  // #703 — the flight guard (#236), the per-place message slots (#479: the
  // page, and each of the four dialogs below by its own scope) and the
  // dialog-session generation (#477 part 2) come from one shared hook; this
  // screen keeps only its idempotency-key and refresh discipline below, and
  // says which scopes own a dialog. The page's "Saved" message clears as each
  // attempt starts, exactly where the old wrapper cleared it.
  const { busy, isPending, errors, run, openDialog, dismissDialog } = useDialogAction(
    DIALOG_SCOPES,
    { onAttempt: () => setMessage(null) },
  );
  const setPageError = errors.setPage;

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
  // #630 — which item is open is the USER's answer, and it can change while a
  // write is in flight: the panel's own Close link carries no `disabled={busy}`
  // and stays reachable behind the dialog for the whole flight (#480). Read it
  // through a ref so `refreshAll` compares against the panel as it stands when
  // its re-read lands, not against the id its handler closed over.
  const activeIdRef = useRef<string | null>(null);
  activeIdRef.current = activeId;
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

  // Dismissal is one of the two session edges (#703): it mutes the attempt
  // still out, so a late failure is not reported against a session the user
  // reopened, and ends the session, so a late success cannot act on it.
  const closeCreate = () => { setCreating(false); dismissDialog("create"); };
  const closeEdit = () => { setEditingId(null); dismissDialog("edit"); };
  const closePurchase = () => { setPurchasing(false); dismissDialog("purchase"); };
  const closeAdjust = () => { setAdjusting(false); dismissDialog("adjust"); };

  // #703 review r2 (PR 2) — create/edit/adjust are admin-gated
  // (`open={… && isAdmin}`), so a role change HIDES them without firing
  // onClose: an in-flight write would stay `current()` and could reset/close
  // the dialog a re-promotion restores. End each session on the isAdmin edge
  // (dismiss mutes + advances the generation; the setter hides it), so a
  // re-promotion reopens a fresh one. Purchase is open to every role.
  useEffect(() => {
    if (!isAdmin) {
      setCreating(false); dismissDialog("create");
      setEditingId(null); dismissDialog("edit");
      setAdjusting(false); dismissDialog("adjust");
    }
  }, [isAdmin, dismissDialog]);

  async function refreshAll() {
    const fresh = await fetchItems();
    setItems(fresh);
    // The list refresh above is the write's business and always applies; the
    // PANEL is the user's. #630 — this used to re-activate the id its caller
    // captured when the handler ran, so a write settling after the user closed
    // the panel (its Close link carries no `disabled={busy}` and stays
    // reachable behind the dialog for the whole flight, #480) put them back on
    // an item they had already left. Reading which item is open NOW, rather
    // than passing the write's own down, removes that staleness instead of
    // testing for it. A `null` here is as authoritative as a row: the panel
    // describes an item the refresh says is gone.
    const openNow = activeIdRef.current;
    if (openNow !== null) {
      const stillThere = fresh.find((i) => i.id === openNow) ?? null;
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

  // The write discipline every mutation on this screen shares: the write under
  // its idempotency key, the items (+ open panel's lots) refresh, and only THEN
  // the key rotation — if the refresh throws, the key survives and a retry
  // replays the idempotent write instead of repeating it. All three are facts
  // about the world: they run whether or not the dialog that started them is
  // still on screen (#703 — the superseded-safe rule). The flight guard, the
  // failure routing and the success gate are the hook's; this helper is called
  // INSIDE `run`, and anything it throws lands in the slot `run` was given.
  // #511 round 2 — `touchesLedger` decides whether this write goes through the
  // LEDGER's runWrite. Only a write that produces an InventoryMovement does.
  // Round 1 wrapped all six, so an unrelated create-item claimed the open
  // ledger's ticket: it disabled that ledger's Load more for the duration
  // (canLoadMore folds in `loading`), re-walked every loaded page for nothing,
  // and could clear a standing ledger error the moment its incidental re-read
  // happened to succeed. FlocksPage already draws this line — only
  // onRecordMovement goes through ledger.runWrite there.
  async function commit(keyScope: string, action: (key: string) => Promise<unknown>, touchesLedger = false): Promise<void> {
    const write = async () => {
      await action(keyFor(keyScope));
      await refreshAll();
    };
    // A ledger write refreshes the whole loaded movement window (AC4); a
    // catalog write refreshes items and lots only.
    if (touchesLedger) await ledger.runWrite(write);
    else await write();
    clearKey(keyScope);
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
    await run("create", async (current) => {
      await commit("create-item", (key) =>
        createInventoryItem({
          name, category, unit,
          defaultUnitCostMinorUnits: toMinorUnits(defaultCost),
        }, key));
      // Superseded (#703): the item exists and the list shows it, but the form
      // reset, the message and the close belong to the session on screen now.
      if (!current()) return;
      setName("");
      setDefaultCost("");
      setMessage(i18n.t("inventory:itemCreatedMessage"));
      setCreating(false);
    });
  }

  function startEdit(i: InventoryItem) {
    closeCreate();
    // Opening is the other session edge (#703): a different item's edit
    // DISPLACES this one without onClose — reachable behind the backdrop via a
    // screen reader's virtual cursor (#480; pi review of #491) — and
    // `openDialog` ends whatever edit session was on screen, unconditionally.
    // The slot is the dialog's ("edit"), not per item, since #703.
    openDialog("edit");
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
    // The run scope is the dialog's; the idempotency key stays per item.
    await run("edit", async (current) => {
      await commit(`update:${id}`, (key) =>
        updateInventoryItem(id, {
          name: editName, unit: editUnit,
          defaultUnitCostMinorUnits: toMinorUnits(editCost),
        }, key));
      // Edit's superseded case is unreachable through the UI (the row's edit
      // button is `disabled={busy}`); the gate stays for INV-1 and against a
      // future change that enables the button — pinned by the wiring test
      // "a successful edit closes its dialog and refreshes the list".
      if (!current()) return;
      setEditingId(null);
    });
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
    // #703 review r2 (PR 2) — a skipped-while-busy submit must not un-mute:
    // Enter bypasses the disabled submit button, `run` would skip the second
    // attempt, but the pre-run `beginAttempt` below would still un-mute an
    // abandoned attempt and let its late failure into the reopened dialog.
    if (busy) return;
    // Clears the purchase slot whether or not the check below fails, so a
    // fixed keystroke doesn't leave a stale verdict behind.
    errors.beginAttempt("purchase");
    const qty = parseFloat(purchaseQty);
    if (!Number.isFinite(qty) || qty <= 0) {
      errors.report("purchase", i18n.t("inventory:quantityMustBePositive"));
      return;
    }
    // The run scope is the dialog's; the idempotency key stays per item.
    await run("purchase", async (current) => {
      await commit(`purchase:${active.id}`, (key) =>
        recordInventoryPurchase(active.id, {
          receivedDate: purchaseDate,
          quantity: qty,
          unitCostMinorUnits: toMinorUnits(purchaseCost),
          lotNumber: lotNumber.trim() || undefined,
          expiryDate: expiryDate || undefined,
          note: purchaseNote.trim() || undefined,
        }, key), true);
      // Superseded (#703): the lot exists, the items/lots/ledger show it and
      // the key rotated; the resets, the message and the close are the
      // replacement session's.
      if (!current()) return;
      setPurchaseQty("");
      setPurchaseCost("");
      setLotNumber("");
      setExpiryDate("");
      setPurchaseNote("");
      setMessage(i18n.t("inventory:purchaseRecordedMessage"));
      setPurchasing(false);
    });
  }

  async function onAdjust(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    // Same reasoning as onPurchase: a skipped-while-busy submit must not
    // un-mute (#703 review r2), and the slot is cleared up front so either
    // guard below reports against a clean slot.
    if (busy) return;
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
    // The run scope is the dialog's; the idempotency key stays per item + lot.
    await run("adjust", async (current) => {
      await commit(`adjust:${active.id}:${adjustLotId}`, (key) =>
        recordInventoryAdjustment(active.id, {
          inventoryLotId: adjustLotId,
          date: today,
          type: adjustType,
          quantityDelta: adjustType === "Discard" ? -Math.abs(delta) : delta,
          reason: adjustReason.trim(),
        }, key), true);
      // Superseded (#703): the correction landed and the view shows it; the
      // resets, the message and the close are the replacement session's.
      if (!current()) return;
      setAdjustQty("");
      setAdjustReason("");
      setMessage(i18n.t("inventory:correctionRecordedMessage"));
      setAdjusting(false);
    });
  }

  const lotLabel = (l: InventoryLot) =>
    `${fmt.date(l.receivedDate)}${l.lotNumber ? ` · ${l.lotNumber}` : ""} — ${fmt.count(l.quantityAvailable)}/${fmt.count(l.quantityReceived)}`;

  const costText = (i: InventoryItem) =>
    i.defaultCostMinorUnits !== null && i.defaultCostCurrencyCode
      ? fmt.money(i.defaultCostMinorUnits, i.defaultCostCurrencyCode,
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
          <button type="button" onClick={() => { closeEdit(); openDialog("create"); setCreating(true); }}>
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
            <BusyButton type="submit" busy={isPending("create")} disabled={busy}>{t("addItemButton")}</BusyButton>
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
          <DialogError errors={errors} scope="edit" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("edit")} disabled={busy}>
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
            <button type="button" onClick={() => { openDialog("purchase"); setPurchasing(true); }}>
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
              <button type="button" className="link" onClick={() => { openDialog("adjust"); setAdjusting(true); }}>
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
                <BusyButton type="submit" busy={isPending("purchase")} disabled={busy}>
                  {t("recordPurchaseSubmitButton")}
                </BusyButton>
              </div>
            </form>
          </Dialog>

          <Dialog open={adjusting && isAdmin} title={t("correctStockDialogTitle", { name: active.name })} onClose={closeAdjust}>
            <form className="form-grid" onSubmit={onAdjust}>
              {/* Disabled during any flight — kept as shipped (#242); since
                  #703 the spinner reads the fixed "adjust" scope, so the
                  original re-pointing hazard is gone, and the field stays
                  inert during a flight like every other trigger here. */}
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
                {/* The pending scope is the dialog's; the composite key scope is
                    the idempotency key's alone since #703. */}
                <BusyButton type="submit" busy={isPending("adjust")}
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
                <tr><th>{t("ledgerDateHeader")}</th><th>{t("ledgerTypeHeader")}</th><th className="num">{t("ledgerQuantityHeader")}</th><th>{t("ledgerNoteHeader")}</th></tr>
              </thead>
              <tbody>
                {ledger.rows.map((m) => (
                  <tr key={m.id}>
                    <td className="nowrap"><FarmDate iso={m.date} /></td>
                    <td>{inventoryMovementLabel(m.type)}</td>
                    <td className="num">{m.quantityDelta > 0 ? `+${fmt.count(m.quantityDelta)}` : fmt.count(m.quantityDelta)} {m.unit}</td>
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
          <tr><th>{t("nameHeader")}</th><th>{t("categoryHeader")}</th><th className="num">{t("onHandHeader")}</th><th className="num">{t("defaultCostHeader")}</th><th>{t("statusHeader")}</th><th></th></tr>
        </thead>
        <tbody>
          {items.map((i) => (
            <tr key={i.id} className={i.active ? undefined : "inactive"}>
              <td>{i.name}</td>
              <td>{inventoryCategoryLabel(i.category)}</td>
              <td className="num">{fmt.count(i.quantityOnHand)} {i.unit}</td>
              <td className="num">{costText(i)}</td>
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
                        onClick={() => void run(`deactivate:${i.id}`, () => commit(`deactivate:${i.id}`, (key) => deactivateInventoryItem(i.id, key)))}>
                        {t("deactivateButton")}
                      </BusyButton>
                    ) : (
                      <BusyButton className="link" busy={isPending(`activate:${i.id}`)} disabled={busy}
                        onClick={() => void run(`activate:${i.id}`, () => commit(`activate:${i.id}`, (key) => activateInventoryItem(i.id, key)))}>
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
