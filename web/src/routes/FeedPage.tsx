import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import {
  formatMoney, listFeedUsage, listFlocks, listInventoryItems, recordFeedUsage,
} from "../api/cluckwork";
import type { FeedUsage, Flock, InventoryItem } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { usePendingAction } from "../components/usePendingAction";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import i18n from "../i18n";

const PAGE = 50;

// Client-side mirror of RecordFeedUsageHandler.FeedableCategories — the same
// list InventoryPage used to gate its (now removed) usage dialog. The server
// re-checks; this only keeps un-feedable items out of the picker.
const FEEDABLE_CATEGORIES = ["Feed", "Supplement", "Additive"];

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
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local (#35/#123): the API judges "future date"
  // against the FARM's day, so the picker's ceiling must agree.
  const today = useFarmToday();
  const [searchParams] = useSearchParams();
  const [rows, setRows] = useState<FeedUsage[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  // Full list (inactive included) resolves history names; the picker filters
  // down to active feedable items.
  const [items, setItems] = useState<InventoryItem[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const { busy, run } = usePendingAction();

  // capture form
  const [flockId, setFlockId] = useState("");
  const [itemId, setItemId] = useState("");
  const [date, setDate] = useState(today);
  const [quantity, setQuantity] = useState("");
  const [note, setNote] = useState("");

  // list filters
  const [flockFilter, setFlockFilter] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

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

  const load = useCallback(async (offset = 0) => {
    const page = await listFeedUsage({
      flockId: flockFilter || undefined,
      from: from || undefined,
      to: to || undefined,
      limit: PAGE,
      offset,
    });
    setHasMore(page.length === PAGE);
    setRows((prev) => (offset === 0 ? page : [...(prev ?? []), ...page]));
  }, [flockFilter, from, to]);

  useEffect(() => {
    Promise.all([
      listFlocks({ includeArchived: true }),
      listInventoryItems({ includeInactive: true }),
    ])
      .then(([allFlocks, allItems]) => {
        setFlocks(allFlocks);
        setItems(allItems);
        const firstActive = allFlocks.find((f) => f.status === "Active")
          ?? allFlocks.find((f) => f.status === "Depleted");
        if (firstActive) setFlockId(firstActive.id);
        const feedable = allItems.filter(
          (x) => x.active && FEEDABLE_CATEGORIES.includes(x.category));
        // ?item= deep link (from the Inventory page's per-item hint) wins
        // when it names a feedable item; otherwise the first feedable one.
        const linked = feedable.find((x) => x.id === searchParams.get("item"));
        const preselected = linked ?? feedable[0];
        if (preselected) setItemId(preselected.id);
      })
      .catch(() => setError(i18n.t("feed:loadFailed")));
    // Mount-only on purpose: the deep link is read once; later navigation
    // within the page must not yank the picker back.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    load().catch(() => setError(i18n.t("feed:loadRecordsFailed")));
  }, [load]);

  const flockName = (id: string) => flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  const itemName = (id: string) => items.find((x) => x.id === id)?.name ?? id.slice(0, 8);
  const pickableFlocks = flocks.filter((f) => f.status !== "Archived");
  const pickableItems = items.filter(
    (x) => x.active && FEEDABLE_CATEGORIES.includes(x.category));
  const selectedItem = items.find((x) => x.id === itemId);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    setError(null);
    setMessage(null);
    const parsed = parseFloat(quantity);
    // Validated before the flight opens: a rejected form never reads as busy.
    if (!Number.isFinite(parsed) || parsed <= 0) {
      setError(i18n.t("feed:quantityMustBePositive"));
      return;
    }
    const scope = `record:${itemId}:${flockId}:${date}`;
    await run(scope, async () => {
      try {
        await recordFeedUsage(itemId,
          { flockId, date, quantity: parsed, note: note.trim() || undefined },
          keyFor(scope));
        setMessage(i18n.t("feed:recordedMessage"));
        await load();
        clearKey(scope);
        setQuantity("");
        setNote("");
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  if (error && rows === null) return <section><h2>{t("title")}</h2><p className="error">{error}</p></section>;
  if (rows === null) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;

  return (
    <section>
      <h2>{t("title")}</h2>
      <p className="muted">{t("intro")}</p>

      <form className="form-grid" onSubmit={onSubmit}>
        <label>{t("flockLabel")}
          <select value={flockId} onChange={(e) => setFlockId(e.target.value)}>
            {pickableFlocks.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name}{f.status === "Depleted" ? t("depletedFlockSuffix") : ""}
              </option>
            ))}
          </select>
        </label>
        <label>{t("itemLabel")}
          <select value={itemId} onChange={(e) => setItemId(e.target.value)}>
            {pickableItems.map((x) => (
              <option key={x.id} value={x.id}>
                {t("itemOption", { name: x.name, onHand: x.quantityOnHand, unit: x.unit })}
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
        <BusyButton type="submit" busy={busy} disabled={!flockId || !itemId}>
          {t("recordFeedButton")}
        </BusyButton>
      </form>

      {/* Feed is create-only — the FIFO stock draw already happened, so a
          mis-entry is undone with a compensating lot adjustment, not an edit. */}
      <p className="muted">{t("correctionsHint")}</p>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <h3>{t("recordsHeading")}</h3>
      <div className="form-grid">
        <label>{t("flockLabel")}
          <select value={flockFilter} onChange={(e) => setFlockFilter(e.target.value)}>
            <option value="">{tc("all")}</option>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <label>{t("fromLabel")}
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>{t("toLabel")}
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      {rows.length === 0 ? (
        <p className="muted">{t("noRecordsMatch")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("dateHeader")}</th><th>{t("flockHeader")}</th><th>{t("itemHeader")}</th><th>{t("amountHeader")}</th><th>{t("estimatedCostHeader")}</th><th>{t("noteHeader")}</th></tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td>{r.date}</td>
                  <td>{flockName(r.flockId)}</td>
                  <td>{itemName(r.inventoryItemId)}</td>
                  <td>{r.quantity} {r.unit}</td>
                  <td>{formatMoney(r.estimatedCostMinorUnits, r.currencyCode, r.currencyMinorUnit)}</td>
                  <td>{r.note ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link" disabled={busy}
              onClick={() => void load(rows.length).catch(() => setError(i18n.t("feed:loadMoreFailed")))}>
              {t("loadMoreButton")}
            </button>
          )}
        </>
      )}
    </section>
  );
}
