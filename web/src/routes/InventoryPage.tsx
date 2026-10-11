import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import {
  Box, Button, Typography, useMediaQuery,
} from "@mui/material";
import {
  getAccount, listInventoryItems, listInventoryLots, listInventoryMovements, parseMoneyToMinorUnits,
} from "../api/cluckwork";
import type { Account, InventoryItem, InventoryLot, InventoryMovement } from "../api/cluckwork";
import { useAuth } from "../auth/useAuth";
import { FieldConsole, ConsoleSubhead } from "../components/FieldConsole";
import { focusPhoneRow } from "../components/PhoneLedger";
import { usePagedList } from "../components/usePagedList";
import { useDialogAction } from "../components/useDialogAction";
import { newId } from "../lib/ids";
import { MD_UP_QUERY } from "../lib/breakpoints";
import { useFarmToday } from "../farm/useFarm";
import { InventoryCatalogDialogs } from "./InventoryCatalogDialogs";
import { InventoryItemPanel } from "./InventoryItemPanel";
import { InventoryCatalog } from "./InventoryCatalog";
import i18n from "../i18n";


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
  const isDesktop = useMediaQuery(MD_UP_QUERY);
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

  // #987 — the phone row's read-only peek. It holds an ID, never a row: the
  // item is re-read from the live list every render, so a refresh can neither
  // leave it showing a stale status nor offering `deactivate` on an item that
  // is already inactive (#988 review r1). Its open-lot count is not on the
  // list response, so it takes its own ticketed read and stays null (an em
  // dash) when that read is superseded or fails: a lot count must never take
  // the peek down with it.
  const [detailsId, setDetailsId] = useState<string | null>(null);
  const [detailsOpenLots, setDetailsOpenLots] = useState<number | null>(null);
  // #988 review r2 — activate/deactivate close the peek and disable its row in
  // the same commit, so `Dialog`'s restore (one retry a frame later) lands on
  // <body> for any request slower than a frame. The row is still the right
  // place to be afterwards: the item stays listed, with its status flipped.
  const focusAfterWrite = useRef<string | null>(null);

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
  }, [setPageError]);

  useEffect(() => {
    if (busy) return;
    const rowId = focusAfterWrite.current;
    if (rowId === null) return;
    focusAfterWrite.current = null;
    focusPhoneRow(rowId);
  }, [busy]);

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

  if (errors.page && items === null) {
    return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="error">{errors.page}</p></FieldConsole>;
  }
  if (items === null) {
    return <FieldConsole><Typography variant="h2">{t("title")}</Typography><p className="muted">{tc("loading")}</p></FieldConsole>;
  }

  // Adjusted during render, the React-documented way to reset state a refreshed
  // list invalidates: an id the catalog no longer holds is retired, so a later
  // load cannot silently reopen the peek on it.
  if (detailsId !== null && !items.some((i) => i.id === detailsId)) setDetailsId(null);

  const intro = <Typography component="p" variant="body2" sx={{ color: "text.secondary" }}>{t("intro")}</Typography>;
  const createItemButton = isAdmin && (
    <Button variant="contained" startIcon={<Plus size={16} aria-hidden />} sx={{ width: { xs: "100%", md: "auto" }, minHeight: 44, my: { xs: 1, md: 0 } }}
      onClick={() => { closeEdit(); openDialog("create"); setCreating(true); }}>
      {t("newItemButton")}
    </Button>
  );

  return (
    <FieldConsole>
      {isDesktop ? (
        <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: 1, alignItems: "start", mb: 2 }}>
          <Box>
            <Typography variant="h2">{t("title")}</Typography>
            {intro}
          </Box>
          {createItemButton}
        </Box>
      ) : (
        // "Feed & inventory" leaves no room beside it at the h2 scale, so the
        // create action keeps its own full-width row — above the intro, where
        // it costs the list nothing (#987).
        <Box sx={{ mb: 2 }}>
          <Typography variant="h2">{t("title")}</Typography>
          {createItemButton}
          {intro}
        </Box>
      )}

      <InventoryCatalogDialogs
        creating={creating} setCreating={setCreating}
        editingId={editingId} setEditingId={setEditingId}
        isAdmin={isAdmin}
        busy={busy} isPending={isPending} errors={errors}
        run={run} commit={commit} setMessage={setMessage}
        name={name} setName={setName} category={category} setCategory={setCategory}
        unit={unit} setUnit={setUnit} defaultCost={defaultCost} setDefaultCost={setDefaultCost}
        editName={editName} setEditName={setEditName} editUnit={editUnit} setEditUnit={setEditUnit}
        editCost={editCost} setEditCost={setEditCost}
        closeCreate={closeCreate} closeEdit={closeEdit}
        toMinorUnits={toMinorUnits} costStep={costStep}
      />

      {/* Unconditional since #479 — a dialog's failure lives in its own slot
          now, so the page copy can't inherit it. */}
      {errors.page && <p className="error">{errors.page}</p>}
      {message && <p className="success">{message}</p>}

      {active && (
        <InventoryItemPanel
          active={active} isAdmin={isAdmin}
          busy={busy} isPending={isPending} errors={errors}
          run={run} commit={commit}
          openDialog={openDialog} purchasing={purchasing} setPurchasing={setPurchasing}
          adjusting={adjusting} setAdjusting={setAdjusting}
          closePurchase={closePurchase} closeAdjust={closeAdjust}
          lots={lots} setAdjustLotId={setAdjustLotId}
          adjustLotId={adjustLotId} adjustType={adjustType} setAdjustType={setAdjustType}
          adjustQty={adjustQty} setAdjustQty={setAdjustQty}
          adjustReason={adjustReason} setAdjustReason={setAdjustReason}
          purchaseDate={purchaseDate} setPurchaseDate={setPurchaseDate}
          purchaseQty={purchaseQty} setPurchaseQty={setPurchaseQty}
          purchaseCost={purchaseCost} setPurchaseCost={setPurchaseCost}
          lotNumber={lotNumber} setLotNumber={setLotNumber}
          expiryDate={expiryDate} setExpiryDate={setExpiryDate}
          purchaseNote={purchaseNote} setPurchaseNote={setPurchaseNote}
          today={today} costStep={costStep} toMinorUnits={toMinorUnits}
          ledger={ledger} setActive={setActive} setMessage={setMessage}
        />
      )}

      {!isDesktop && <ConsoleSubhead title={t("itemsHeading")} caption={t("itemsCaption")} />}
      <InventoryCatalog
        items={items} isDesktop={isDesktop} isAdmin={isAdmin}
        busy={busy} isPending={isPending} run={run} commit={commit}
        onOpen={onOpen} startEdit={startEdit} minorUnit={minorUnit}
        detailsId={detailsId} setDetailsId={setDetailsId}
        detailsOpenLots={detailsOpenLots} setDetailsOpenLots={setDetailsOpenLots}
        focusAfterWrite={focusAfterWrite}
      />
    </FieldConsole>
  );
}
