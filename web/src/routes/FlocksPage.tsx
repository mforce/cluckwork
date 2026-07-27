import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import {
  archiveFlock, createFlock, depleteFlock, listBirdMovements, listFlocks, reactivateFlock,
  recordBirdMovement, updateFlock,
} from "../api/cluckwork";
import type { BirdMovement, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { StatusBadge } from "../components/StatusBadge";
import { useConfirm } from "../components/useConfirm";
import { usePendingAction } from "../components/usePendingAction";
import { useAuth } from "../auth/useAuth";
import { ageWeeks } from "../lib/dates";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { flockMovementLabel, statusLabel } from "../i18n/enums";

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F7 (#47): manage flocks — create, correct identity fields, deplete, archive.
// Archived flocks leave pickers and the dashboard; this screen still shows them
// behind a toggle. Current bird count math is the mortality slice, not this one.
export function FlocksPage() {
  const { t } = useTranslation("flocks");
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  // Creating a flock records the day's work (birds arrived); corrections,
  // lifecycle changes, and manual movements are admin-only (#73).
  const { isAdmin } = useAuth();
  const { confirm, confirmDialog } = useConfirm();
  const [flocks, setFlocks] = useState<Flock[] | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // #236: the flight guard + per-scope spinner state live in the shared hook;
  // this screen keeps only its idempotency-key and refresh discipline below.
  const { busy, isPending, run: runPending } = usePendingAction();

  // create form (F131: in a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [breed, setBreed] = useState("");
  const [placed, setPlaced] = useState(today);
  const [count, setCount] = useState(100);

  // edit — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editBreed, setEditBreed] = useState("");
  const [editPlaced, setEditPlaced] = useState("");
  const [editCount, setEditCount] = useState(0);

  // bird ledger (#54): one flock's movements open at a time
  const [ledgerFlockId, setLedgerFlockId] = useState<string | null>(null);
  const [movements, setMovements] = useState<BirdMovement[] | null>(null);
  const [mvDate, setMvDate] = useState(today);
  const [mvType, setMvType] = useState("Cull");
  const [mvQty, setMvQty] = useState(1);
  const [mvNote, setMvNote] = useState("");
  const [recording, setRecording] = useState(false); // F131: movement capture in a dialog

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

  const fetchFlocks = useCallback(
    () => listFlocks({ includeArchived: true, limit: 500 }),
    [],
  );

  useEffect(() => {
    fetchFlocks()
      .then(setFlocks)
      .catch(() => setError(i18n.t("flocks:loadFlocksFailed")));
  }, [fetchFlocks]);

  async function run(scope: string, action: (key: string) => Promise<unknown>): Promise<boolean> {
    const outcome = await runPending(scope, async () => {
      setError(null);
      try {
        await action(keyFor(scope));
        // Refresh must succeed before the key rotates (grade-management review
        // lesson): if it throws, a retry replays the idempotent write.
        setFlocks(await fetchFlocks());
        clearKey(scope);
        return true;
      } catch (err) {
        setError(errorMessage(err));
        return false;
      }
    });
    // A skipped run (another flight already open) reports `undefined` — never
    // success: mapping it to false keeps a blocked submit from closing its
    // dialog or resetting its form as if it had saved.
    return outcome ?? false;
  }

  // F135: the two lifecycle changes ask first. Named handlers rather than the
  // inline row lambdas they replace, because the ask is now awaited.
  async function onDeplete(f: Flock) {
    const ok = await confirm({
      title: i18n.t("flocks:depleteConfirmTitle", { name: f.name }),
      body: i18n.t("flocks:depleteConfirmBody"),
      confirmLabel: i18n.t("flocks:depleteConfirmLabel"),
      destructive: true,
    });
    if (ok) await run(`deplete:${f.id}`, (key) => depleteFlock(f.id, key));
  }

  async function onArchive(f: Flock) {
    const ok = await confirm({
      title: i18n.t("flocks:archiveConfirmTitle", { name: f.name }),
      body: i18n.t("flocks:archiveConfirmBody"),
      confirmLabel: i18n.t("flocks:archiveConfirmLabel"),
      destructive: true,
    });
    if (ok) await run(`archive:${f.id}`, (key) => archiveFlock(f.id, key));
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-flock", (key) =>
      createFlock({ name, breed, placementDate: placed, initialCount: count }, key));
    if (ok) {
      setName("");
      setBreed("");
      setPlaced(today);
      setCount(100);
      setCreating(false);
    }
  }

  function startEdit(f: Flock) {
    setError(null);
    setCreating(false);
    setEditingId(f.id);
    setEditName(f.name);
    setEditBreed(f.breed);
    setEditPlaced(f.placementDate);
    setEditCount(f.initialCount);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    const ok = await run(`update:${id}`, (key) =>
      updateFlock(id, {
        name: editName, breed: editBreed,
        placementDate: editPlaced, initialCount: editCount,
      }, key));
    if (ok) setEditingId(null);
  }

  // Guards the async fetch: only the ledger currently open may write state,
  // so a slow response for flock A can't render under flock B's heading.
  const ledgerRequest = useRef<string | null>(null);

  async function openLedger(id: string) {
    setRecording(false); // a movement dialog belongs to the ledger that opened it
    if (ledgerFlockId === id) {
      setLedgerFlockId(null);
      ledgerRequest.current = null;
      return;
    }
    setLedgerFlockId(id);
    setMovements(null);
    setMvDate(today);
    ledgerRequest.current = id;
    try {
      const rows = await listBirdMovements(id, { limit: 50 });
      if (ledgerRequest.current === id) setMovements(rows);
    } catch {
      if (ledgerRequest.current === id) setError(i18n.t("flocks:loadMovementsFailed"));
    }
  }

  async function onRecordMovement(e: FormEvent) {
    e.preventDefault();
    if (!ledgerFlockId) return;
    const id = ledgerFlockId;
    const ok = await run(`movement:${id}`, async (key) => {
      await recordBirdMovement(id, {
        date: mvDate, type: mvType, quantity: mvQty,
        note: mvNote || undefined,
      }, key);
      const rows = await listBirdMovements(id, { limit: 50 });
      if (ledgerRequest.current === id) setMovements(rows);
    });
    if (ok) {
      setMvQty(1);
      setMvNote("");
      setRecording(false);
    }
  }

  if (error && flocks === null) {
    return <section><h2>{t("title")}</h2><p className="error">{error}</p></section>;
  }
  if (flocks === null) {
    return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  }

  const visible = flocks.filter((f) => showArchived || f.status !== "Archived");
  const archivedCount = flocks.filter((f) => f.status === "Archived").length;

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        <button type="button" onClick={() => { setError(null); setEditingId(null); setCreating(true); }}>
          <Plus size={16} aria-hidden /> {t("newFlockButton")}
        </button>
      </div>
      <p className="muted">
        {t("intro")}
      </p>

      <Dialog open={creating} title={t("newFlockDialogTitle")} onClose={() => setCreating(false)}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>{t("nameLabel")}
            <input value={name} required maxLength={100}
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>{t("breedLabel")}
            <input value={breed} required maxLength={100}
              onChange={(e) => setBreed(e.target.value)} />
          </label>
          <label>{t("placedLabel")}
            <input type="date" value={placed} max={today} required
              onChange={(e) => setPlaced(e.target.value)} />
          </label>
          <label>{t("birdsLabel")}
            <input className="cell" type="number" min={1} value={count} required
              onChange={(e) => setCount(Math.max(1, e.target.valueAsNumber || 1))} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("create-flock")} disabled={busy}>{t("addFlockButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      {/* Editing is admin-only, so a role change mid-edit closes it. */}
      <Dialog open={editingId !== null && isAdmin} title={t("editFlockDialogTitle")} onClose={() => setEditingId(null)}>
        {/* noValidate: the row's save used to be a plain button — native
            constraint validation never ran on these fields. */}
        <form className="inline-form" noValidate onSubmit={onSaveEdit}>
          <label>{t("editNameLabel")}
            <input value={editName} maxLength={100}
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <label>{t("editBreedLabel")}
            <input value={editBreed} maxLength={100}
              onChange={(e) => setEditBreed(e.target.value)} />
          </label>
          <label>{t("editPlacedLabel")}
            <input type="date" value={editPlaced} max={today}
              onChange={(e) => setEditPlaced(e.target.value)} />
          </label>
          <label>{t("editCountLabel")}
            <input className="cell" type="number" min={1} value={editCount}
              onChange={(e) => setEditCount(Math.max(1, e.target.valueAsNumber || 1))} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setEditingId(null)}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={editingId !== null && isPending(`update:${editingId}`)} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </div>
        </form>
      </Dialog>

      {/* A dialog renders its own copy of the error; don't double it. */}
      {error && !creating && editingId === null && !recording && <p className="error">{error}</p>}

      {archivedCount > 0 && (
        <label className="muted check">
          <input type="checkbox" checked={showArchived}
            onChange={(e) => setShowArchived(e.target.checked)} />
          {t("showArchivedLabel", { count: archivedCount })}
        </label>
      )}

      {visible.length === 0 ? (
        <p className="muted">{t("noFlocksMessage")}</p>
      ) : (
        <table className="data">
          <thead>
            <tr>
              <th>{t("nameHeader")}</th><th>{t("breedHeader")}</th><th>{t("placedHeader")}</th><th>{t("ageHeader")}</th>
              <th>{t("birdsHeader")}</th><th>{t("statusHeader")}</th><th></th>
            </tr>
          </thead>
          <tbody>
            {visible.map((f) => (
              <tr key={f.id} className={f.status === "Archived" ? "inactive" : undefined}>
                <td>{f.name}</td>
                <td>{f.breed}</td>
                <td>{f.placementDate}</td>
                <td>{t("ageWeeksSuffix", { weeks: ageWeeks(f.placementDate) })}</td>
                <td>
                  {f.currentBirds}
                  {f.currentBirds !== f.initialCount &&
                    <span className="muted"> / {f.initialCount}</span>}
                </td>
                <td><StatusBadge status={f.status} label={statusLabel(f.status)} /></td>
                <td>
                  <button className="link" disabled={busy}
                    onClick={() => void openLedger(f.id)}>
                    {ledgerFlockId === f.id ? t("closeLedgerButton") : t("openLedgerButton")}
                  </button>
                  {isAdmin && (
                    <BusyButton className="link" busy={isPending(`update:${f.id}`)} disabled={busy}
                      onClick={() => startEdit(f)}>{t("editButton")}</BusyButton>
                  )}
                  {isAdmin && f.status === "Active" && (
                    <BusyButton className="link" busy={isPending(`deplete:${f.id}`)} disabled={busy}
                      onClick={() => void onDeplete(f)}>
                      {t("depleteButton")}
                    </BusyButton>
                  )}
                  {isAdmin && f.status !== "Archived" && (
                    // After the confirm dialog settles, THIS button is the
                    // pending indicator for the in-flight archive (#236).
                    <BusyButton className="link" busy={isPending(`archive:${f.id}`)} disabled={busy}
                      onClick={() => void onArchive(f)}>
                      {t("archiveButton")}
                    </BusyButton>
                  )}
                  {isAdmin && f.status !== "Active" && (
                    // The undo (#57): back to Active, full capture restored.
                    <BusyButton className="link" busy={isPending(`reactivate:${f.id}`)} disabled={busy}
                      onClick={() => void run(`reactivate:${f.id}`, (key) => reactivateFlock(f.id, key))}>
                      {t("reactivateButton")}
                    </BusyButton>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {ledgerFlockId && (
        <div className="order-panel">
          <h3>
            {t("ledgerHeading", { name: flocks.find((f) => f.id === ledgerFlockId)?.name ?? "" })}
          </h3>
          <p className="muted">
            {t("ledgerIntro")}
            {isAdmin ? t("ledgerIntroAdminNote") : t("ledgerIntroWorkerNote")}
          </p>

          {isAdmin && (
            <button type="button" onClick={() => { setError(null); setRecording(true); }}>
              <Plus size={16} aria-hidden /> {t("recordMovementButton")}
            </button>
          )}

          <Dialog open={recording && isAdmin} title={t("recordMovementDialogTitle")} onClose={() => setRecording(false)}>
            <form className="inline-form" onSubmit={onRecordMovement}>
              <label>{t("dateLabel")}
                <input type="date" value={mvDate} max={today}
                  onChange={(e) => setMvDate(e.target.value)} />
              </label>
              <label>{t("typeLabel")}
                <select value={mvType} onChange={(e) => setMvType(e.target.value)}>
                  <option value="Cull">{flockMovementLabel("Cull")}</option>
                  <option value="Adjustment">{flockMovementLabel("Adjustment")}</option>
                </select>
              </label>
              <label>{t("birdsLabel")}
                <input className="cell" type="number" value={mvQty}
                  min={mvType === "Cull" ? 1 : undefined}
                  onChange={(e) => setMvQty(e.target.valueAsNumber || 0)} />
              </label>
              <label>{t("noteLabel")}
                <input value={mvNote} maxLength={500}
                  onChange={(e) => setMvNote(e.target.value)} />
              </label>
              {error && <p className="error">{error}</p>}
              <div className="dialog-foot">
                <button type="button" className="link" onClick={() => setRecording(false)}>{tc("cancel")}</button>
                <BusyButton type="submit"
                  busy={ledgerFlockId !== null && isPending(`movement:${ledgerFlockId}`)}
                  disabled={busy || mvQty === 0}>
                  {t("recordButton")}
                </BusyButton>
              </div>
            </form>
          </Dialog>

          {movements === null ? (
            <p className="muted">{tc("loading")}</p>
          ) : movements.length === 0 ? (
            <p className="muted">{t("noMovementsMessage")}</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>{t("ledgerDateHeader")}</th><th>{t("ledgerTypeHeader")}</th><th>{t("ledgerBirdsHeader")}</th><th>{t("ledgerNoteHeader")}</th></tr>
              </thead>
              <tbody>
                {movements.map((m) => (
                  <tr key={m.id}>
                    <td>{m.date}</td>
                    <td>{flockMovementLabel(m.type)}</td>
                    <td>{m.quantity > 0 ? `−${m.quantity}` : `+${-m.quantity}`}</td>
                    <td>{m.note ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {confirmDialog}
    </section>
  );
}
