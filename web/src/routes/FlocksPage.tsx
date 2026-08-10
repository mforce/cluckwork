import { useCallback, useEffect, useId, useRef, useState } from "react";
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
import { DialogError } from "../components/DialogError";
import { NumberField } from "../components/NumberField";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { StatusBadge } from "../components/StatusBadge";
import { useConfirm } from "../components/useConfirm";
import { useDialogErrors } from "../components/useDialogErrors";
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
  // #479 — one slot per PLACE a message can appear: the page (mount load and
  // the bird-ledger read), and each dialog by its own scope.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;
  // #236: the flight guard + per-scope spinner state live in the shared hook;
  // this screen keeps only its idempotency-key and refresh discipline below.
  const { busy, isPending, run: runPending } = usePendingAction();

  // create form (F131: in a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [breed, setBreed] = useState("");
  const [placed, setPlaced] = useState(today);
  const [count, setCount] = useState(100);
  // NumberField owns its own input, so labels point at it by id (#250).
  const fieldId = useId();
  const idFor = (name: string) => `${fieldId}-${name}`;

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
      .catch(() => setPageError(i18n.t("flocks:loadFlocksFailed")));
  }, [fetchFlocks, setPageError]);

  // `errorScope` names the DIALOG a failure belongs to; `null` routes it to the
  // page. Deplete/archive/reactivate run from row buttons, not a dialog, so
  // they pass `null` — same place the old shared `error` used to render them.
  async function run(
    scope: string,
    errorScope: string | null,
    action: (key: string) => Promise<unknown>,
  ): Promise<boolean> {
    const outcome = await runPending(scope, async () => {
      errors.beginAttempt(errorScope);
      try {
        await action(keyFor(scope));
        // Refresh must succeed before the key rotates (grade-management review
        // lesson): if it throws, a retry replays the idempotent write.
        setFlocks(await fetchFlocks());
        clearKey(scope);
        return true;
      } catch (err) {
        errors.report(errorScope, errorMessage(err));
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
    if (ok) await run(`deplete:${f.id}`, null, (key) => depleteFlock(f.id, key));
  }

  async function onArchive(f: Flock) {
    const ok = await confirm({
      title: i18n.t("flocks:archiveConfirmTitle", { name: f.name }),
      body: i18n.t("flocks:archiveConfirmBody"),
      confirmLabel: i18n.t("flocks:archiveConfirmLabel"),
      destructive: true,
    });
    if (ok) await run(`archive:${f.id}`, null, (key) => archiveFlock(f.id, key));
  }

  // Dismissal empties this dialog's slot and mutes the attempt still out, so a
  // late failure is not reported against a session the user reopened.
  const closeCreate = () => { setCreating(false); errors.abandon("create"); };

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-flock", "create", (key) =>
      createFlock({ name, breed, placementDate: placed, initialCount: count }, key));
    if (ok) {
      setName("");
      setBreed("");
      setPlaced(today);
      setCount(100);
      setCreating(false);
    }
  }

  const closeEdit = () => { setEditingId(null); errors.abandon("edit"); };

  function startEdit(f: Flock) {
    closeCreate(); // defensive: New flock and Edit are mutually exclusive triggers
    // A different flock's edit DISPLACES this one — the session ends without
    // onClose, so nothing else abandons the fixed "edit" scope, and the
    // displaced flock's verdict would render inside the next flock's dialog.
    // Reachable behind the backdrop via a screen reader's virtual cursor
    // (#480; pi review of #491). Same-flock re-entry is not a displacement.
    if (editingId !== null && editingId !== f.id) errors.abandon("edit");
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
    const ok = await run(`update:${id}`, "edit", (key) =>
      updateFlock(id, {
        name: editName, breed: editBreed,
        placementDate: editPlaced, initialCount: editCount,
      }, key));
    if (ok) setEditingId(null);
  }

  // Guards the async fetch: only the ledger currently open may write state,
  // so a slow response for flock A can't render under flock B's heading.
  const ledgerRequest = useRef<string | null>(null);

  const closeRecordMovement = () => { setRecording(false); errors.abandon("record-movement"); };

  async function openLedger(id: string) {
    closeRecordMovement(); // a movement dialog belongs to the ledger that opened it
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
      if (ledgerRequest.current === id) setPageError(i18n.t("flocks:loadMovementsFailed"));
    }
  }

  async function onRecordMovement(e: FormEvent) {
    e.preventDefault();
    if (!ledgerFlockId) return;
    const id = ledgerFlockId;
    const ok = await run(`movement:${id}`, "record-movement", async (key) => {
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

  if (errors.page && flocks === null) {
    return <section><h2>{t("title")}</h2><p className="error">{errors.page}</p></section>;
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
        <button type="button" onClick={() => { closeEdit(); setCreating(true); }}>
          <Plus size={16} aria-hidden /> {t("newFlockButton")}
        </button>
      </div>
      <p className="muted">
        {t("intro")}
      </p>

      <Dialog open={creating} title={t("newFlockDialogTitle")} onClose={closeCreate}>
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
          {/* #250: sibling label, not wrapping — a <label> may not contain
              interactive content other than its own control, and the stepper
              carries two buttons. */}
          <div className="numfield-field">
            <label htmlFor={idFor("birds")}>{t("birdsLabel")}</label>
            <NumberField id={idFor("birds")} label={t("birdsLabel").toLowerCase()}
              value={count} onChange={setCount} min={1} />
          </div>
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("create-flock")} disabled={busy}>{t("addFlockButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      {/* Editing is admin-only, so a role change mid-edit closes it. */}
      <Dialog open={editingId !== null && isAdmin} title={t("editFlockDialogTitle")} onClose={closeEdit}>
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
          <div className="numfield-field">
            <label htmlFor={idFor("edit-count")}>{t("editCountLabel")}</label>
            <NumberField id={idFor("edit-count")} label={t("editCountLabel").toLowerCase()}
              value={editCount} onChange={setEditCount} min={1} />
          </div>
          <DialogError errors={errors} scope="edit" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={editingId !== null && isPending(`update:${editingId}`)} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </div>
        </form>
      </Dialog>

      {/* #479 — unconditional: each dialog now renders its own failure through
          its own slot (DialogError above), so nothing here can be a stale copy
          of a dialog's message; this is only ever the page's own. */}
      {errors.page && <p className="error">{errors.page}</p>}

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
              <th>{t("birdsHeader")}</th><th>{t("statusHeader")}</th>
              <th>{tc("recordHistoryHeader")}</th><th></th>
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
                <ProvenanceCell history={f} />
                <td>
                  <button className="link" disabled={busy}
                    onClick={() => void openLedger(f.id)}>
                    {ledgerFlockId === f.id ? t("closeLedgerButton") : t("openLedgerButton")}
                  </button>
                  {isAdmin && (
                    // Opens the edit dialog — non-mutating, so the spinner
                    // belongs to the dialog's Save, not here (#242).
                    <button className="link" disabled={busy}
                      onClick={() => startEdit(f)}>{t("editButton")}</button>
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
                      onClick={() => void run(`reactivate:${f.id}`, null, (key) => reactivateFlock(f.id, key))}>
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
            <button type="button" onClick={() => setRecording(true)}>
              <Plus size={16} aria-hidden /> {t("recordMovementButton")}
            </button>
          )}

          <Dialog open={recording && isAdmin} title={t("recordMovementDialogTitle")} onClose={closeRecordMovement}>
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
              {/* A cull removes at least one bird; an Adjustment counts both
                  ways (added or lost), so its floor is unbounded (#250). */}
              <div className="numfield-field">
                <label htmlFor={idFor("mv-birds")}>{t("birdsLabel")}</label>
                <NumberField id={idFor("mv-birds")} label={t("birdsLabel").toLowerCase()}
                  value={mvQty} onChange={setMvQty}
                  min={mvType === "Cull" ? 1 : Number.NEGATIVE_INFINITY} />
              </div>
              <label>{t("noteLabel")}
                <input value={mvNote} maxLength={500}
                  onChange={(e) => setMvNote(e.target.value)} />
              </label>
              <DialogError errors={errors} scope="record-movement" />
              <div className="dialog-foot">
                <button type="button" className="link" onClick={closeRecordMovement}>{tc("cancel")}</button>
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
