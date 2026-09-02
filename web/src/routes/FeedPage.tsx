import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import {
  listFeedUsage, listFlocks, listInventoryItems, recordFeedUsage,
} from "../api/cluckwork";
import type { Flock, InventoryItem } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { BusyButton } from "../components/BusyButton";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { usePagedList } from "../components/usePagedList";
import { usePendingAction } from "../components/usePendingAction";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import i18n from "../i18n";

const PAGE = 50;

// Client-side mirror of RecordFeedUsageHandler.FeedableCategories — one copy
// for the SPA (InventoryPage imports it for its panel link). The server
// re-checks; this only keeps un-feedable items out of the picker.
export const FEEDABLE_CATEGORIES = ["Feed", "Supplement", "Additive"];

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #446 — feed usage promoted to a first-class page (it was a dialog buried in
// the Inventory drill-down while its sibling, Water, had a page). Same shape
// as WaterPage minus in-place corrections: feed is create-only by design —
// the stock effect lives in the lots/movement ledger, so a mis-entry is
// corrected with a compensating Inventory ADJUSTMENT, not an edit here.
export function FeedPage() {
  const { t } = useTranslation("feed");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local (#35/#123): the API judges "future date"
  // against the FARM's day, so the picker's ceiling must agree.
  const today = useFarmToday();
  const [searchParams] = useSearchParams();
  const [flocks, setFlocks] = useState<Flock[]>([]);
  // Full list (inactive included) resolves history names; the picker filters
  // down to active feedable items.
  const [items, setItems] = useState<InventoryItem[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const { busy, run } = usePendingAction();

  // capture form
  // #512 (T027/T038) — the capture flock is committed through FlockPicker.
  // `captureFlock` is the page-controlled committed entity; bumping
  // `captureFlockGen` re-syncs the engine's committed state after an external
  // reset (the mount-time default) so a later Escape cannot resurrect a stale
  // ID. `flockSnapshot.canSubmit` gates BOTH the Record button and onSubmit.
  const [captureFlock, setCaptureFlock] = useState<Flock | null>(null);
  const [captureFlockGen, setCaptureFlockGen] = useState(0);
  const [captureFlockSnapshot, setCaptureFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  const [capturePickerOpen, setCapturePickerOpen] = useState(false);
  const [itemId, setItemId] = useState("");
  const [date, setDate] = useState(today);
  const [quantity, setQuantity] = useState("");
  const [note, setNote] = useState("");

  // list filters — initialized from the URL so the Daily Entry strip's
  // "Feed: N records" link lands on exactly the day it was describing.
  // #512 (T038) — the URL-named flock is a ROW-OWNED identity: the list is
  // filtered by its EXACT id, and its name is resolved through the picker's
  // exact GET (never substituted with the first discovery result). A scoped
  // 404/transport failure enters the explicit `unavailable` state: the filter
  // keeps the exact id, the name shows the translated unavailable label, and
  // Retry re-issues ONLY the GET.
  const [flockFilter, setFlockFilter] = useState(() => searchParams.get("flockId") ?? "");
  // #512 (T038) — the row-owned identity's committed entity (set when the
  // picker's requestedId effect resolves it via the exact GET, or when the
  // user commits a new filter through the picker). The trigger shows
  // `flockFilterEntity?.name` (the exact name) or the translated unavailable
  // label (when the snapshot's phase is `unavailable`).
  const [flockFilterEntity, setFlockFilterEntity] = useState<Flock | null>(null);
  const [flockFilterSnapshot, setFlockFilterSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true,
  });
  const [filterPickerOpen, setFilterPickerOpen] = useState(false);
  const [from, setFrom] = useState(() => searchParams.get("from") ?? "");
  const [to, setTo] = useState(() => searchParams.get("to") ?? "");

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

  // #469 — the sequencing this screen grew for itself now lives in
  // usePagedList, shared with every paged screen: only the newest flight may
  // touch rows/hasMore or surface an error, a failed reload clears the rows
  // rather than leaving the previous filter's under the new controls, and the
  // pager is withdrawn for the duration of a reload.
  const usage = usePagedList({
    fetchPage: useCallback(
      (offset: number, limit: number) => listFeedUsage({
        flockId: flockFilter || undefined,
        from: from || undefined,
        to: to || undefined,
        limit,
        offset,
      }),
      [flockFilter, from, to],
    ),
    pageSize: PAGE,
    errorText: () => i18n.t("feed:loadRecordsFailed"),
  });

  useEffect(() => {
    Promise.all([
      listFlocks({ includeArchived: true }),
      listInventoryItems({ includeInactive: true }),
    ])
      .then(([allFlocks, allItems]) => {
        setFlocks(allFlocks);
        setItems(allItems);
        // The capture default is committed as a full typed entity through the
        // picker's controlled sync — the engine admits an entity that is in
        // the discovery window as-is (no spurious exact GET).
        const firstActive = allFlocks.find((f) => f.status === "Active")
          ?? allFlocks.find((f) => f.status === "Depleted");
        if (firstActive) {
          setCaptureFlock(firstActive);
          setCaptureFlockGen((g) => g + 1);
        }
        const feedable = allItems.filter(
          (x) => FEEDABLE_CATEGORIES.includes(x.category)
            && (x.active || x.quantityOnHand > 0));
        // ?item= deep link (from the Inventory page's per-item hint) ALWAYS
        // wins when it names a feedable-category item — even one that has
        // gone inactive AND empty. Substituting feedable[0] there would let
        // a user following a stale link drain an unrelated item; keeping the
        // requested one selected lets the stock check refuse the submit.
        const linked = allItems.find(
          (x) => x.id === searchParams.get("item")
            && FEEDABLE_CATEGORIES.includes(x.category));
        const preselected = linked ?? feedable[0];
        if (preselected) setItemId(preselected.id);
      })
      .catch(() => setError(i18n.t("feed:loadFailed")));
    // Mount-only on purpose: the deep link is read once; later navigation
    // within the page must not yank the picker back.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);


  // #512 (T038) — the filter's displayed name prefers the EXACT committed
  // entity (the row-owned identity, resolved by GET even when it is outside
  // the capped list); a not-yet-resolved id falls back to the full list's
  // name, and an unresolved one shows the explicit unavailable label.
  const flockName = (id: string) => {
    if (id === flockFilter && flockFilterSnapshot.selectionPhase === "unavailable")
      return t("filterFlockUnavailable");
    if (id === flockFilter && flockFilterEntity)
      return flockFilterEntity.name;
    return flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  };

  // #512 (T038) — the filter's unavailable state is owned by the picker's
  // engine (the `requestedId` effect resolves the row-owned id via the exact
  // GET, and a failure enters the engine's `unavailable` phase, which renders
  // the translated label + a keyboard-reachable Retry). The Retry re-issues
  // ONLY the GET (the engine's own `retryUnavailable`), never the records
  // list and never a first-result substitution. The page's `flockName` reads
  // the snapshot's `selectionPhase` to render the explicit unavailable label
  // in the records table's flock column; `flockFilterEntity` holds the exact
  // committed entity once the GET resolves.
  const itemName = (id: string) => items.find((x) => x.id === id)?.name ?? id.slice(0, 8);
  // Deactivation only stops NEW stock from arriving — an inactive item's
  // remaining feed still gets eaten out, exactly as the server allows, so it
  // stays pickable while stock remains (quality review of #446: the old
  // Inventory dialog allowed this and the deep link must not silently swap
  // to a different item).
  // The deep-linked item stays listed even when inactive+empty, so the
  // selection above always has a visible option behind it.
  const requestedItemId = searchParams.get("item");
  const pickableItems = items.filter(
    (x) => FEEDABLE_CATEGORIES.includes(x.category)
      && (x.active || x.quantityOnHand > 0 || x.id === requestedItemId));
  const selectedItem = items.find((x) => x.id === itemId);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    // #512 (T027) — the picker's canSubmit is the write guard, not the
    // button's disabled attribute: an exploring/unavailable picker must not
    // submit a stale committed flock even if the control is bypassed.
    if (busy || !captureFlockSnapshot.canSubmit || !captureFlock) return;
    setError(null);
    setMessage(null);
    const parsed = parseFloat(quantity);
    // Validated before the flight opens: a rejected form never reads as busy.
    if (!Number.isFinite(parsed) || parsed <= 0) {
      setError(i18n.t("feed:quantityMustBePositive"));
      return;
    }
    const scope = `record:${itemId}:${captureFlock.id}:${date}`;
    await run(scope, async () => {
      try {
        // runWrite claims the list's ticket BEFORE the POST, so a filter
        // change made while it is in flight is newer intent and keeps the
        // view; the refresh stands down instead of repainting the old
        // filter's rows over it (#469 — this screen had it backwards).
        await usage.runWrite(async () => {
          await recordFeedUsage(itemId,
            { flockId: captureFlock.id, date, quantity: parsed, note: note.trim() || undefined },
            keyFor(scope));
          setMessage(i18n.t("feed:recordedMessage"));
          // The picker's "(N kg on hand)" is the user's pre-submit sanity
          // check — refresh it or it lies after one feeding.
          setItems(await listInventoryItems({ includeInactive: true }));
        });
        clearKey(scope);
        setQuantity("");
        setNote("");
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  if (error && usage.rows === null) return <section><h2>{t("title")}</h2><p className="error">{error}</p></section>;
  if (usage.rows === null) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;

  return (
    <section>
      <h2>{t("title")}</h2>
      <p className="muted">{t("intro")}</p>

      <form className="form-grid" onSubmit={onSubmit}>
        <FlockPicker
          label={t("flockLabel")}
          eligibility="active-and-depleted"
          required
          open={capturePickerOpen}
          controlledCommitted={captureFlock}
          controlledGeneration={captureFlockGen}
          onSnapshot={setCaptureFlockSnapshot}
          onCommit={(f) => {
            setCaptureFlock(f);
            setCaptureFlockGen((g) => g + 1);
            setCapturePickerOpen(false);
          }}
          onEscape={() => setCapturePickerOpen(false)}
          onOutsideClick={() => setCapturePickerOpen(false)}
          trigger={
            <button
              type="button"
              className="named-picker-trigger"
              onClick={() => setCapturePickerOpen(true)}
            >
              {captureFlock
                ? `${captureFlock.name}${captureFlock.status === "Depleted" ? t("depletedFlockSuffix") : ""}`
                : t("selectFlockOption")}
            </button>
          }
        />
        <label>{t("itemLabel")}
          <select value={itemId} onChange={(e) => setItemId(e.target.value)}>
            {pickableItems.map((x) => (
              <option key={x.id} value={x.id}>
                {t("itemOption", { name: x.name, onHand: x.quantityOnHand, unit: x.unit })}
                {x.active ? ""
                  : x.quantityOnHand > 0 ? t("inactiveItemSuffix")
                    : t("inactiveEmptyItemSuffix")}
              </option>
            ))}
          </select>
        </label>
        <label>{t("dateLabel")}
          <input type="date" value={date} max={today} required
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>
          {selectedItem
            ? t("quantityLabelWithUnit", { unit: selectedItem.unit })
            : t("quantityLabel")}
          <input type="number" min={0.001} step={0.001} value={quantity} required
            onChange={(e) => setQuantity(e.target.value)} />
        </label>
        <label>{t("noteLabel")}
          <input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
        </label>
        <BusyButton type="submit" busy={busy}
          disabled={!captureFlock || !captureFlockSnapshot.canSubmit || !itemId}>
          {t("recordFeedButton")}
        </BusyButton>
      </form>

      {/* Feed is create-only — the FIFO stock draw already happened, so a
          mis-entry is undone with a compensating lot adjustment, not an edit. */}
      <p className="muted">{t("correctionsHint")}</p>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <h3>{t("recordsHeading")}</h3>
      {/* List failures degrade the LIST only — the capture form must stay
          usable through a transient history read failure (review of #446). */}
      {usage.error && <p className="error">{usage.error}</p>}
      <div className="form-grid">
        <div className="filter-flock">
          <FlockPicker
            label={t("filterFlockLabel")}
            eligibility="all"
            required={false}
            open={filterPickerOpen}
            requestedId={flockFilter || null}
            onSnapshot={(snap) => {
              setFlockFilterSnapshot(snap);
              // The engine's requestedId effect commits the resolved entity
              // (or enters the unavailable phase). Track the committed
              // entity so the trigger and the records table's flock column
              // can render the EXACT name (never a first-result
              // substitution).
              if (snap.committed) setFlockFilterEntity(snap.committed);
            }}
            onCommit={(f) => {
              setFlockFilter(f.id);
              setFlockFilterEntity(f);
              setFilterPickerOpen(false);
            }}
            onClear={() => {
              setFlockFilter("");
              setFlockFilterEntity(null);
            }}
            onEscape={() => setFilterPickerOpen(false)}
            onOutsideClick={() => setFilterPickerOpen(false)}
            trigger={
              <button type="button" className="named-picker-trigger"
                onClick={() => setFilterPickerOpen(true)}>
                {flockFilter === "" ? tc("all") : flockName(flockFilter)}
              </button>
            }
          />
        </div>
        <label>{t("fromLabel")}
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>{t("toLabel")}
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      {/* One window's rows must never sit under another window's controls,
          not even for the length of the request (#469). Only this region is
          gated — the capture form above must stay usable throughout. */}
      {usage.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : usage.rows.length === 0 ? (
        <p className="muted">{t("noRecordsMatch")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("dateHeader")}</th><th>{t("flockHeader")}</th><th>{t("itemHeader")}</th><th className="num">{t("amountHeader")}</th><th className="num">{t("estimatedCostHeader")}</th><th>{t("noteHeader")}</th></tr>
            </thead>
            <tbody>
              {usage.rows.map((r) => (
                <tr key={r.id}>
                  <td className="nowrap">{fmt.date(r.date)}</td>
                  <td>{r.flockName ?? t("rowFlockUnavailable")}</td>
                  <td>{itemName(r.inventoryItemId)}</td>
                  <td className="num">{fmt.count(r.quantity)} {r.unit}</td>
                  <td className="num">{fmt.money(r.estimatedCostMinorUnits, r.currencyCode, r.currencyMinorUnit)}</td>
                  <td>{r.note ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {usage.canLoadMore && (
            // Two rapid clicks cannot append the same page twice: the hook
            // no-ops a load-more while one is in flight, and canLoadMore
            // withdraws the control for the duration (review of #446).
            <button className="link" disabled={busy}
              onClick={() => void usage.loadMore()}>
              {t("loadMoreButton")}
            </button>
          )}
        </>
      )}
    </section>
  );
}
